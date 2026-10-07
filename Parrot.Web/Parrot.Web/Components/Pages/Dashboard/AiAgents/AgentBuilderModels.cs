using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

// Aurora's agent builder works on a richer agent than Parrot stores. Parrot's
// AiAgent has a name, description, mascot, human-takeover flag, channels and
// playbooks; everything else Aurora asks for (role, boundaries, voice, tests)
// is held here for the session and never written to AiAgentsState.

public enum AgentRoleTemplate { Sales, Support, Orders, Custom }

public enum AgentPermission
{
    AnswerQuestions,
    RecommendProducts,
    LookUpOrders,
    CollectDetails,
    CreateDiscounts,
    ApproveRefunds
}

public enum AgentHandoffTarget { GeneralTeam, SalesTeam, SupportTeam, WorkspaceAdmins }

public enum AgentVoicePreset { Warm, Professional, Upbeat }

public enum AgentResponseLength { Concise, Balanced, Detailed }

public enum AgentEmojiUsage { None, Light, Expressive }

public enum AgentTestScenarioId { BusinessQuestion, HumanRequest, UnknownQuestion, OrderStatus, Custom }

public enum AgentOrientationStatus { NotStarted, InProgress, Ready }

public enum AgentSectionStatus { NotStarted, Ready }

public enum AgentTestStatus { NotStarted, InProgress, Passed }

/// <summary>A knowledge source the builder can point an agent at. Parrot has no
/// knowledge store yet, so these only exist for the session.</summary>
public sealed class AgentKnowledgeSource
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;

    /// <summary>"website" or "document" — the two kinds the builder can add.</summary>
    public string Type { get; init; } = "website";
}

/// <summary>What Aurora calls the business knowledge summary. The business
/// name is Parrot's (the signed-in workspace); readiness, topics and gaps have
/// no Parrot source and stay unknown.</summary>
public sealed class AgentKnowledgeWorkspace
{
    public string BusinessName { get; set; } = string.Empty;
    public List<AgentKnowledgeSource> Sources { get; } = new();
}

public sealed class AgentSetupDraft
{
    /// <summary>The Parrot agent being edited, or null while creating a new one.</summary>
    public string? AgentId { get; set; }

    /// <summary>True once the identity step has been saved — Aurora's "currentAgent".</summary>
    public bool IdentitySaved { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentRoleTemplate RoleTemplate { get; set; } = AgentRoleTemplate.Support;
    public string Mascot { get; set; } = "Nova";

    public List<string> KnowledgeSourceIds { get; set; } = new();
    public AgentOrientationStatus OrientationStatus { get; set; } = AgentOrientationStatus.NotStarted;

    public AgentSectionStatus BoundaryStatus { get; set; } = AgentSectionStatus.NotStarted;
    public List<string> Channels { get; set; } = new() { "whatsapp", "instagram", "facebook" };
    public List<AgentPermission> AllowedActions { get; set; } = new();
    public int ConfidenceThreshold { get; set; } = 70;
    public bool NegativeSentiment { get; set; } = true;
    public bool RepeatedFailure { get; set; } = true;
    public bool HighValueTransactions { get; set; }
    public int HighValueThresholdNgn { get; set; } = 50000;
    public AgentHandoffTarget HandoffTarget { get; set; } = AgentHandoffTarget.GeneralTeam;

    public AgentSectionStatus VoiceStatus { get; set; } = AgentSectionStatus.NotStarted;
    public AgentVoicePreset VoicePreset { get; set; } = AgentVoicePreset.Warm;
    public AgentResponseLength ResponseLength { get; set; } = AgentResponseLength.Balanced;
    public AgentEmojiUsage EmojiUsage { get; set; } = AgentEmojiUsage.Light;
    public bool UseCustomerName { get; set; } = true;
    public string Greeting { get; set; } = string.Empty;
    public List<string> AvoidPhrases { get; set; } = new();

    public AgentTestStatus TestStatus { get; set; } = AgentTestStatus.NotStarted;
    public List<AgentTestScenarioId> CompletedTestScenarioIds { get; set; } = new();
    public int TestRunCount { get; set; }
}

public sealed record AgentTestCheck(string Label, string Detail, bool Handoff);

/// <summary>One sandbox run. Parrot has no answer engine, so every run is
/// resolved by the draft's own handoff rules and never carries a reply or a
/// confidence score.</summary>
public sealed class AgentTestResult
{
    public AgentTestScenarioId ScenarioId { get; init; }
    public bool Handoff { get; init; } = true;
    public string CustomerMessage { get; init; } = string.Empty;
    public string? Reply { get; init; }
    public string Reason { get; init; } = string.Empty;
    public AgentHandoffTarget HandoffTarget { get; init; }
    public IReadOnlyList<AgentTestCheck> Checks { get; init; } = Array.Empty<AgentTestCheck>();
}

public static class AgentBuilderCopy
{
    public static string HandoffLabel(AgentHandoffTarget target) => target switch
    {
        AgentHandoffTarget.SalesTeam => "Sales team",
        AgentHandoffTarget.SupportTeam => "Support team",
        AgentHandoffTarget.WorkspaceAdmins => "Workspace admins",
        _ => "General team queue"
    };

    public static string MascotImage(string mascot) => mascot switch
    {
        "Atlas" => "/assets/images/avatars/avatar-2.png",
        "Bolt" => "/assets/images/avatars/avatar-3.png",
        "Aegis" => "/assets/images/avatars/avatar-4.png",
        "Buzz" => "/assets/images/avatars/avatar-5.png",
        "Luna" => "/assets/images/avatars/avatar-6.png",
        _ => "/assets/images/avatars/avatar-1.png"
    };

    public static readonly IReadOnlyList<string> Mascots = ["Nova", "Atlas", "Bolt", "Aegis", "Buzz", "Luna"];

    /// <summary>The channels an agent can answer on, as owners call them.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Channels =
        [("whatsapp", "WhatsApp"), ("instagram", "Instagram"), ("facebook", "Facebook")];

    public static string ChannelName(AgentChannel channel) =>
        Channels.FirstOrDefault(c => string.Equals(c.Key, channel.Key, StringComparison.OrdinalIgnoreCase)).Name ?? channel.Name;

    /// <summary>"WhatsApp, Instagram and Facebook".</summary>
    public static string ChannelList(AiAgent agent)
    {
        // In the order owners see them everywhere else.
        var names = agent.Channels.Where(c => c.IsEnabled)
            .OrderBy(c => Channels.ToList().FindIndex(k => string.Equals(k.Key, c.Key, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue)
            .Select(ChannelName).ToList();
        return names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    /// <summary>The channel rows AiAgentsState seeds on a new agent, with the
    /// same keys, names and copy, so the detail page reads the same whichever
    /// path created the agent.</summary>
    public static List<AgentChannel> DefaultChannels(IEnumerable<string> enabledKeys)
    {
        var enabled = new HashSet<string>(enabledKeys, StringComparer.OrdinalIgnoreCase);
        return
        [
            new() { Key = "whatsapp", Name = "WhatsApp Business", Description = "Allow this agent to respond to conversations from this channel.", IsEnabled = enabled.Contains("whatsapp") },
            new() { Key = "facebook", Name = "facebook Marketplace", Description = "Allow this agent to respond to conversations from this channel.", IsEnabled = enabled.Contains("facebook") },
            new() { Key = "instagram", Name = "Instagram Dm", Description = "Allow this agent to respond to conversations from this channel.", IsEnabled = enabled.Contains("instagram") }
        ];
    }
}

/// <summary>
/// A job an agent can be set up for, in the owner's words: what it is called
/// on the setup screen, what it does, how it opens a chat and how it ends an
/// answer, and the avatar it suggests. Owners can have one agent per role.
/// </summary>
public sealed record AgentRole(
    AgentRoleTemplate Id,
    string Name,
    string Does,
    string Description,
    IconName Icon,
    string Opening,
    string Closing,
    string Mascot,
    IReadOnlyList<KnowledgeTopic> Asks)
{
    public static readonly IReadOnlyList<AgentRole> All =
    [
        new(AgentRoleTemplate.Support, "Customer support", "Questions and problems",
            "Answers customer questions and sorts out problems, kindly and quickly.",
            IconName.RiCustomerService2Fill,
            "How can I help today?", "Is there anything else I can help with?", "Nova",
            [KnowledgeTopic.Hours, KnowledgeTopic.Returns, KnowledgeTopic.Delivery]),
        new(AgentRoleTemplate.Sales, "Sales & growth", "Helps customers choose and buy",
            "Helps customers find the right thing and buy it.",
            IconName.RiShoppingBag3Fill,
            "Looking for something? I can help you find it.", "Would you like help placing an order?", "Atlas",
            [KnowledgeTopic.Products, KnowledgeTopic.Prices, KnowledgeTopic.Payment]),
        new(AgentRoleTemplate.Orders, "Orders & products", "Products, orders and delivery",
            "Answers questions about products, orders and delivery.",
            IconName.RiTruckFill,
            "Ask me about any product or your order.", "Anything else about your order?", "Bolt",
            [KnowledgeTopic.Delivery, KnowledgeTopic.Products, KnowledgeTopic.Payment]),
        new(AgentRoleTemplate.Custom, "Build my own", "Describe the job yourself",
            "Helps your customers.",
            IconName.RiSparkling2Fill,
            "How can I help?", "Is there anything else I can help with?", "Luna",
            [KnowledgeTopic.Products, KnowledgeTopic.Hours, KnowledgeTopic.Delivery]),
    ];

    public static AgentRole Of(AgentRoleTemplate? id) => All.FirstOrDefault(r => r.Id == id) ?? All[0];

    /// <summary>A job as owners read it: the role's name, or the owner's own name for Build my own.</summary>
    public static string NameOf(AgentSettings settings) =>
        settings.Role != AgentRoleTemplate.Custom ? Of(settings.Role).Name
        : string.IsNullOrWhiteSpace(settings.CustomRole) ? "Your own job" : settings.CustomRole.Trim();
}
