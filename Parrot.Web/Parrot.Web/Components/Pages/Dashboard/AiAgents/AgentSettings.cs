using System.Text.Json;
using Parrot.Web.Features.Dashboard.AiAgents;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

/// <summary>A connected workspace channel that an agent may answer on.</summary>
public sealed record AgentChannelChoice(string Key, string Name, string? AccountName);

/// <summary>The topics of Advanced settings, which a basic rule's Change opens.</summary>
public enum AgentTopic { Talk, Actions, Handoff, Knowledge }

/// <summary>
/// What an agent does and how, beyond what Parrot stores (name, description,
/// mascot, channels): the basics chosen on the setup screen, and the advanced
/// settings on the agent's page. Frontend only until the agents API has
/// them; see agents-backend-requests.
/// </summary>
public sealed class AgentSettings
{
    // ── Basics ──────────────────────────────────────────────────────────────

    /// <summary>The job it was set up for; owners can have one agent per role.</summary>
    public AgentRoleTemplate Role { get; set; } = AgentRoleTemplate.Support;

    /// <summary>Build my own: the job's name, in the owner's words ("Bookings"). Its description is the agent's.</summary>
    public string CustomRole { get; set; } = string.Empty;

    /// <summary>
    /// Who it passes a customer to, in words: the owner's own (before teams), or the choice below described
    /// ("Customer service, shared evenly"). The AI reads this. Empty: the owner, or anyone on the team.
    /// </summary>
    public string HandoverTo { get; set; } = string.Empty;

    /// <summary>
    /// Who it brings in (HandoverModes): anyone on the team, a role, or people. Empty: not chosen yet, so anyone on the
    /// team (the default), or an older agent's own words in HandoverTo. Working alone, always the owner.
    /// </summary>
    public string HandoverMode { get; set; } = string.Empty;

    /// <summary>With a role: the role whose people share its handovers and tasks evenly.</summary>
    public string? HandoverRoleId { get; set; }

    /// <summary>With people: who shares its handovers and tasks evenly (user ids).</summary>
    public List<string> HandoverPeople { get; set; } = [];

    public bool CanOfferDiscounts { get; set; }
    public bool CanApproveRefunds { get; set; }

    // ── Its job description (setup Concept B) ──────────────────────────────

    /// <summary>The job it was created for (AgentUseCase.Id); null for agents set up in Concept A.</summary>
    public string? UseCase { get; set; }

    /// <summary>The kind of business (AgentIndustry.Id), which shaped its first draft.</summary>
    public string? Industry { get; set; }

    /// <summary>The business's website, read for knowledge the owner then reviews.</summary>
    public string Website { get; set; } = string.Empty;

    /// <summary>Its job description, in the owner's words: its job, what it does, never does, and when it brings them in.</summary>
    public AgentBrief? Brief { get; set; }

    // ── How it talks ────────────────────────────────────────────────────────

    public AgentVoicePreset Tone { get; set; } = AgentVoicePreset.Warm;
    public AgentResponseLength Length { get; set; } = AgentResponseLength.Balanced;
    public AgentEmojiUsage Emoji { get; set; } = AgentEmojiUsage.Light;

    /// <summary>Its first message, in the owner's words. Empty: one written for its role.</summary>
    public string Greeting { get; set; } = string.Empty;

    /// <summary>Words and phrases it never says, one each, as the owner added them.</summary>
    public List<string> AvoidPhrases { get; set; } = [];

    // ── What it can do ──────────────────────────────────────────────────────

    private bool _canAccessProductsAndServices = true;
    private bool _canRecommend = true;

    // Keep existing demo agents' access until changed. Turning access back on does not grant recommendations.
    public bool CanAccessProductsAndServices
    {
        get => _canAccessProductsAndServices;
        set
        {
            _canAccessProductsAndServices = value;
            if (!value) _canRecommend = false;
        }
    }

    public bool CanRecommend
    {
        get => _canRecommend;
        set => _canRecommend = CanAccessProductsAndServices && value;
    }
    public bool CanCheckOrders { get; set; }

    /// <summary>Asks for a name and phone number before handing over, so the team can follow up.</summary>
    public bool CanTakeDetails { get; set; } = true;

    // ── When it hands over (asking for a person always does) ────────────────

    public bool HandOverWhenUpset { get; set; } = true;
    public bool HandOverAfterMisses { get; set; } = true;
    public bool HandOverBigOrders { get; set; }
    public int BigOrderNaira { get; set; } = 50000;
    /// <summary>Currency of the saved threshold; null retains legacy NGN. The old amount property is kept for compatibility.</summary>
    public string? BigOrderCurrency { get; set; }

    // ── What else it knows ──────────────────────────────────────────────────

    /// <summary>What only this agent should know; the business's knowledge is shared by every agent.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Files added to those notes, by name (a price list, a booking policy). Frontend only until the API takes uploads.</summary>
    public List<string> NoteFiles { get; set; } = [];

    /// <summary>Topics of the shared knowledge this agent leaves out. None by default: it uses everything taught.</summary>
    public List<KnowledgeTopic> LeftOut { get; set; } = [];

    /// <summary>Whether it answers from a topic of the shared knowledge.</summary>
    public bool Uses(KnowledgeTopic topic) => !LeftOut.Contains(topic);

    public AgentSettings Copy()
    {
        var copy = (AgentSettings)MemberwiseClone();
        copy.NoteFiles = [.. NoteFiles];
        copy.LeftOut = [.. LeftOut];
        copy.AvoidPhrases = [.. AvoidPhrases];
        copy.HandoverPeople = [.. HandoverPeople];
        copy.Brief = Brief?.Copy();
        return copy;
    }

    /// <summary>
    /// What a job means for how it works: its tone and reply length, what it
    /// can do and when it hands over. Choosing a job sets these (and nothing
    /// about its name or look), so a non-technical owner gets sensible
    /// settings for that job without opening Advanced settings.
    /// </summary>
    public void UseDefaultsFor(AgentRoleTemplate role)
    {
        (Tone, Length) = role switch
        {
            AgentRoleTemplate.Sales => (AgentVoicePreset.Upbeat, AgentResponseLength.Balanced),
            AgentRoleTemplate.Orders => (AgentVoicePreset.Professional, AgentResponseLength.Concise),
            _ => (AgentVoicePreset.Warm, AgentResponseLength.Balanced),
        };
        // Sales suggests what to buy; support and orders look orders up.
        CanRecommend = role is AgentRoleTemplate.Sales or AgentRoleTemplate.Orders or AgentRoleTemplate.Custom;
        CanCheckOrders = role is AgentRoleTemplate.Support or AgentRoleTemplate.Orders;
        CanTakeDetails = true;
        HandOverWhenUpset = true;
        HandOverAfterMisses = true;
        // A person closes large sales.
        // Only with an amount: a workspace outside naira starts without one, and "orders over 0" would hand over every order.
        HandOverBigOrders = role == AgentRoleTemplate.Sales && BigOrderNaira > 0;
    }
}

/// <summary>
/// An agent being set up or changed: everything the setup screen and the
/// agent's settings edit, so neither touches the agent until it is saved.
/// </summary>
public sealed class AgentForm
{
    public string Name { get; set; } = string.Empty;

    public string Mascot { get; set; } = "Nova";

    /// <summary>The owner changed an advanced setting: choosing another job no longer resets them.</summary>
    public bool Tuned { get; set; }

    /// <summary>A custom role's job, in the owner's words.</summary>
    public string Job { get; set; } = string.Empty;

    /// <summary>A job has been chosen. A new agent starts without one, so setup asks that first and shows the rest after.</summary>
    public bool HasRole { get; set; }

    public HashSet<string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AgentSettings Settings { get; set; } = new();

    public AgentRole Role => AgentRole.Of(Settings.Role);

    /// <summary>The job as owners read it: the role's name, or the owner's own for Build my own.</summary>
    public string RoleName => AgentUseCase.NameOf(Settings);

    /// <summary>Build my own needs its job named; the others are named already.</summary>
    public bool HasRoleName => Settings.Role != AgentRoleTemplate.Custom || !string.IsNullOrWhiteSpace(Settings.CustomRole);

    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public string DisplayName => HasName ? Name.Trim() : "your agent";

    /// <summary>
    /// A new agent's starting point: no job and no name yet (usability
    /// testing, 4 October: a job already chosen and filled in left owners
    /// unsure what to do), a look no other agent wears, and every channel no
    /// live agent answers yet; on the first agent, all of them.
    /// </summary>
    public static AgentForm Suggested(IReadOnlyList<AiAgent> others, IEnumerable<string>? availableChannels = null)
    {
        var form = new AgentForm { Mascot = AgentLooks.Free(others.Select(a => a.Mascot)) };
        var available = availableChannels is null
            ? AgentBuilderCopy.Channels.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : availableChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in AgentBuilderCopy.Channels.Where(c => available.Contains(c.Key) && Covering(others, c.Key).Count == 0))
            form.Channels.Add(channel.Key);
        return form;
    }

    /// <summary>A job: its sensible settings come with it (never a name), unless the owner has tuned them.</summary>
    public void Choose(AgentRole role, IReadOnlyList<AiAgent> others)
    {
        Settings.Role = role.Id;
        HasRole = true;
        if (!Tuned) Settings.UseDefaultsFor(role.Id);
    }

    public void ChooseAvatar(string mascot) => Mascot = mascot;
    /// <summary>A character and colour from the look picker, saved as its short text.</summary>
    public void ChooseLook(Parrot.SharedComponents.DesignSystem.AvatarAppearance look) => Mascot = AgentLooks.Save(look);

    /// <summary>The names of live agents already answering on a channel.</summary>
    public static List<string> Covering(IReadOnlyList<AiAgent> others, string key) => others
        .Where(a => a.IsActive && !a.IsDraft && a.Channels.Any(c => c.IsEnabled && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
        .Select(a => a.Name)
        .ToList();

    public static AgentForm From(AiAgent agent)
    {
        var form = new AgentForm
        {
            Name = agent.Name,
            Mascot = agent.Mascot,
            HasRole = true,
            // Its settings are its own: another job keeps them.
            Tuned = true,
            Job = agent.Settings.Role == AgentRoleTemplate.Custom ? agent.Description : string.Empty,
            Settings = agent.Settings.Copy(),
        };
        foreach (var channel in agent.Channels.Where(c => c.IsEnabled)) form.Channels.Add(channel.Key);
        return form;
    }

    /// <summary>A new agent from this form: turned on, or a draft.</summary>
    public AiAgent ToAgent(bool live)
    {
        var agent = new AiAgent
        {
            IsDraft = !live,
            IsActive = live,
            // A person can always step in; who that is, is in the settings.
            HumanTakeover = true,
            Channels = AgentBuilderCopy.DefaultChannels(Channels),
        };
        ApplyTo(agent);
        return agent;
    }

    public void ApplyTo(AiAgent agent)
    {
        var job = Job.Trim();
        agent.Name = Name.Trim();
        agent.Mascot = Mascot;
        agent.Description = Settings.Role == AgentRoleTemplate.Custom && job.Length > 0 ? job : Role.Description;
        // Created from a job description: the whole of it, in the owner's
        // words. The agents API keeps the description (up to 2,000
        // characters, as the job description box) but not the job
        // description yet, so this is how it survives a reload and reaches
        // the AI (see agents-backend-requests). Created first and not told its job
        // yet (designer, 19 October 2026): no description, never the job's stock one.
        if (Settings.Brief is { } brief) agent.Description = brief.Summary.Trim();
        agent.Settings = Settings.Copy();
        foreach (var channel in agent.Channels)
            channel.IsEnabled = Channels.Contains(channel.Key);
    }

    /// <summary>Everything that would be saved, to tell whether anything changed.</summary>
    public string Snapshot() => JsonSerializer.Serialize(new
    {
        Name = Name.Trim(),
        Mascot,
        Job = Job.Trim(),
        Channels = Channels.Select(c => c.ToLowerInvariant()).Order(),
        Settings,
    });
}
