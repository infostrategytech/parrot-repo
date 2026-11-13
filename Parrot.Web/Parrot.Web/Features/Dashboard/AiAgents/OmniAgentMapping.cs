using Parrot.Application.DTOs.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>An agent as the AI service returns it. Only the fields the screens use are read.</summary>
public sealed record OmniAgent(
    string AgentId,
    string Name,
    string UseCase,
    string? Role,
    string Description,
    string? Avatar,
    bool HumanTakeover,
    string Brief,
    string Industry,
    string Website,
    string Status,
    bool IsLive,
    OmniAgentSettings Settings,
    int Version,
    DateTimeOffset CreatedAt);

/// <summary>The service's settings; enum values are lower case (warm, concise, none).</summary>
public sealed record OmniAgentSettings(
    string Tone,
    string Length,
    string Emoji,
    string Greeting,
    List<string> AvoidPhrases,
    string HandoverTo,
    bool CanOfferDiscounts,
    bool CanApproveRefunds,
    bool CanRecommend,
    bool CanCheckOrders,
    bool CanTakeDetails,
    bool HandOverWhenUpset,
    bool HandOverAfterMisses,
    bool HandOverBigOrders,
    int BigOrderNaira,
    string Notes,
    List<string> LeftOut,
    // Asked of the AI service (agents-backend-requests, 11 November 2026), sent only when set, so a service that
    // doesn't know them yet sees the same request as before. Who it brings in: team (the default, not sent),
    // role or people; and the big-order amount's currency (ISO code; absent means NGN).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? HandoverMode = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? HandoverRoleId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    List<string>? HandoverPeople = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? BigOrderCurrency = null);

/// <summary>A Parrot connection assigned to an agent. Platform is whatsapp, facebook, instagram or tiktok.</summary>
public sealed record OmniChannel(Guid ConnectionId, string AgentId, string Platform, bool Enabled, int Version);

public sealed record OmniCreateAgent(
    string Name,
    string UseCase,
    string? Role,
    string Description,
    string Avatar,
    bool HumanTakeover,
    string Brief,
    string Industry,
    string Website,
    List<Guid> ConnectionIds,
    OmniAgentSettings Settings);

/// <summary>Everything the screens own about an agent; role null clears it. Never status, live or channels.</summary>
public sealed record OmniUpdateAgent(
    string Name,
    string UseCase,
    string? Role,
    string Description,
    string Avatar,
    bool HumanTakeover,
    string Brief,
    string Industry,
    string Website,
    OmniAgentSettings Settings);

/// <summary>
/// The screens' agent and the AI service's, each way. The job (use_case) is the service's
/// job-role id, which the setup screen's use cases share; agents set up before use cases had
/// one by role. The settings go through <see cref="AgentMapping"/>'s names, lower-cased.
/// </summary>
public static class OmniAgentMapping
{
    public static AiAgent ToAgent(OmniAgent agent, IEnumerable<string> answersOn)
    {
        var settings = AgentMapping.FromDto(new AgentSettingsDto
        {
            Role = RoleOf(agent.UseCase).ToString(),
            CustomRole = agent.Role ?? string.Empty,
            HandoverTo = agent.Settings.HandoverTo,
            CanOfferDiscounts = agent.Settings.CanOfferDiscounts,
            CanApproveRefunds = agent.Settings.CanApproveRefunds,
            Tone = agent.Settings.Tone,
            Length = agent.Settings.Length,
            Emoji = agent.Settings.Emoji,
            Greeting = agent.Settings.Greeting,
            AvoidPhrases = agent.Settings.AvoidPhrases,
            CanRecommend = agent.Settings.CanRecommend,
            CanCheckOrders = agent.Settings.CanCheckOrders,
            CanTakeDetails = agent.Settings.CanTakeDetails,
            HandOverWhenUpset = agent.Settings.HandOverWhenUpset,
            HandOverAfterMisses = agent.Settings.HandOverAfterMisses,
            HandOverBigOrders = agent.Settings.HandOverBigOrders,
            BigOrderNaira = agent.Settings.BigOrderNaira,
            Notes = agent.Settings.Notes,
            LeftOut = agent.Settings.LeftOut,
        });
        // Who it brings in and the amount's currency, once the service keeps them.
        settings.HandoverMode = agent.Settings.HandoverMode ?? string.Empty;
        settings.HandoverRoleId = agent.Settings.HandoverRoleId;
        settings.HandoverPeople = agent.Settings.HandoverPeople is { } people ? [.. people] : [];
        settings.BigOrderCurrency = agent.Settings.BigOrderCurrency;
        settings.UseCase = agent.UseCase;
        settings.Industry = agent.Industry.Length == 0 ? null : agent.Industry;
        settings.Website = agent.Website;
        settings.Brief = agent.Brief.Length == 0 ? null : new AgentBrief { Summary = agent.Brief };

        return new AiAgent
        {
            Id = agent.AgentId,
            Name = agent.Name,
            Description = agent.Description,
            Mascot = AgentMapping.MascotOf(agent.Avatar ?? string.Empty),
            IsActive = agent.IsLive,
            IsDraft = agent.Status == "draft",
            HumanTakeover = agent.HumanTakeover,
            Channels = AgentBuilderCopy.DefaultChannels(answersOn),
            Settings = settings,
        };
    }

    public static OmniCreateAgent ToCreate(AiAgent agent, IEnumerable<Guid> connectionIds) => new(
        agent.Name.Trim(), UseCaseOf(agent.Settings), CustomRoleOf(agent.Settings), agent.Description, AgentLooks.Key(agent.Mascot),
        agent.HumanTakeover, agent.Settings.Brief?.Summary ?? string.Empty, agent.Settings.Industry ?? string.Empty,
        agent.Settings.Website, [.. connectionIds], ToSettings(agent.Settings));

    public static OmniUpdateAgent ToUpdate(AiAgent agent) => new(
        agent.Name.Trim(), UseCaseOf(agent.Settings), CustomRoleOf(agent.Settings), agent.Description, AgentLooks.Key(agent.Mascot),
        agent.HumanTakeover, agent.Settings.Brief?.Summary ?? string.Empty, agent.Settings.Industry ?? string.Empty,
        agent.Settings.Website, ToSettings(agent.Settings));

    public static OmniAgentSettings ToSettings(AgentSettings settings)
    {
        var dto = AgentMapping.ToDto(settings);
        var mode = settings.HandoverMode is HandoverModes.Role or HandoverModes.People ? settings.HandoverMode : null;
        var currency = Parrot.SharedComponents.DesignSystem.CurrencyAmount.Code(settings.BigOrderCurrency);
        return new OmniAgentSettings(
            dto.Tone.ToLowerInvariant(), dto.Length.ToLowerInvariant(), dto.Emoji.ToLowerInvariant(), dto.Greeting,
            dto.AvoidPhrases, dto.HandoverTo, dto.CanOfferDiscounts, dto.CanApproveRefunds, dto.CanRecommend,
            dto.CanCheckOrders, dto.CanTakeDetails, dto.HandOverWhenUpset, dto.HandOverAfterMisses,
            dto.HandOverBigOrders, dto.BigOrderNaira, dto.Notes, dto.LeftOut,
            HandoverMode: mode,
            HandoverRoleId: mode == HandoverModes.Role ? settings.HandoverRoleId : null,
            HandoverPeople: mode == HandoverModes.People ? [.. settings.HandoverPeople] : null,
            BigOrderCurrency: currency == "NGN" ? null : currency);
    }

    /// <summary>The job-role id: the use case it was set up for, or its role's for older agents.</summary>
    public static string UseCaseOf(AgentSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.UseCase) ? settings.UseCase : settings.Role switch
        {
            AgentRoleTemplate.Sales => "sales",
            AgentRoleTemplate.Orders => "tracking",
            AgentRoleTemplate.Custom => "custom",
            _ => "support",
        };

    /// <summary>The service's job roles carry the screens' role as legacy_role; these are theirs.</summary>
    public static AgentRoleTemplate RoleOf(string useCase) => useCase switch
    {
        "support" => AgentRoleTemplate.Support,
        "sales" or "followups" => AgentRoleTemplate.Sales,
        "tracking" => AgentRoleTemplate.Orders,
        _ => AgentRoleTemplate.Custom,
    };

    private static string? CustomRoleOf(AgentSettings settings) =>
        string.IsNullOrWhiteSpace(settings.CustomRole) ? null : settings.CustomRole.Trim();
}
