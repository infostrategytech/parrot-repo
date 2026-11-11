using Parrot.Application.DTOs.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The screens' agent and the API's, each way. The API keeps the mascot as
/// the image it shows and the settings' choices by name; the screens keep a
/// mascot name and enums. Nothing is lost on a round trip except the
/// playbooks and the note files, which the API does not carry yet.
/// </summary>
public static class AgentMapping
{
    // Parrot's own agents API keeps the amount in naira only; the AI service is asked for the currency
    // (OmniAgentMapping), so only this older path refuses another currency rather than save it as naira.
    public static AgentInfo ToInfo(AiAgent agent)
    {
        RefuseOtherCurrency(agent.Settings);
        return new()
        {
        Name = agent.Name,
        Description = agent.Description,
        // A character look is kept as its own short text; an old mascot as its picture, as before.
        MascotUrl = AgentLooks.IsLook(agent.Mascot) ? agent.Mascot : AgentBuilderCopy.MascotImage(agent.Mascot),
        SupportsHumanTakeover = agent.HumanTakeover,
        Channels = agent.Channels.Where(c => c.IsEnabled).Select(c => c.Key.ToLowerInvariant()).Distinct().ToList(),
        Settings = ToDto(agent.Settings),
        IsDraft = agent.IsDraft,
        };
    }

    public static AiAgent ToAgent(AgentResponse response) => new()
    {
        Id = response.Id.ToString(),
        Name = response.Name,
        Description = response.Description,
        Mascot = MascotOf(response.MascotUrl),
        IsActive = response.IsActive,
        HumanTakeover = response.SupportsHumanTakeover,
        Channels = AgentBuilderCopy.DefaultChannels(response.Channels),
        IsDraft = response.IsDraft,
        Settings = FromDto(response.Settings),
    };

    public static AgentSettingsDto ToDto(AgentSettings settings) => new()
    {
        Role = settings.Role.ToString(),
        CustomRole = settings.CustomRole,
        HandoverTo = settings.HandoverTo,
        CanOfferDiscounts = settings.CanOfferDiscounts,
        CanApproveRefunds = settings.CanApproveRefunds,
        Tone = settings.Tone.ToString(),
        Length = settings.Length.ToString(),
        Emoji = settings.Emoji.ToString(),
        Greeting = settings.Greeting,
        AvoidPhrases = [.. settings.AvoidPhrases],
        CanRecommend = settings.CanRecommend,
        CanCheckOrders = settings.CanCheckOrders,
        // Always on now: there is no switch, and not asking is said in the job (designer, 3 November 2026).
        CanTakeDetails = true,
        HandOverWhenUpset = settings.HandOverWhenUpset,
        HandOverAfterMisses = settings.HandOverAfterMisses,
        HandOverBigOrders = settings.HandOverBigOrders,
        BigOrderNaira = settings.BigOrderNaira,
        Notes = settings.Notes,
        LeftOut = settings.LeftOut.Select(t => t.ToString()).ToList(),
    };

    // A name the screens do not know reads as the default, so a settings
    // document written by a newer API never stops an agent from opening.
    public static AgentSettings FromDto(AgentSettingsDto? dto)
    {
        dto ??= new AgentSettingsDto();
        return new AgentSettings
        {
            Role = Parse(dto.Role, AgentRoleTemplate.Support),
            CustomRole = dto.CustomRole,
            HandoverTo = dto.HandoverTo,
            CanOfferDiscounts = dto.CanOfferDiscounts,
            CanApproveRefunds = dto.CanApproveRefunds,
            Tone = Parse(dto.Tone, AgentVoicePreset.Warm),
            Length = Parse(dto.Length, AgentResponseLength.Balanced),
            Emoji = Parse(dto.Emoji, AgentEmojiUsage.Light),
            Greeting = dto.Greeting,
            AvoidPhrases = [.. dto.AvoidPhrases],
            CanRecommend = dto.CanRecommend,
            CanCheckOrders = dto.CanCheckOrders,
            CanTakeDetails = dto.CanTakeDetails,
            HandOverWhenUpset = dto.HandOverWhenUpset,
            HandOverAfterMisses = dto.HandOverAfterMisses,
            HandOverBigOrders = dto.HandOverBigOrders,
            BigOrderNaira = dto.BigOrderNaira,
            Notes = dto.Notes,
            LeftOut = dto.LeftOut
                .Select(name => Enum.TryParse<KnowledgeTopic>(name, true, out var topic) ? topic : (KnowledgeTopic?)null)
                .Where(topic => topic is not null)
                .Select(topic => topic!.Value)
                .Distinct()
                .ToList(),
        };
    }

    /// <summary>A character look as saved, or the old mascot whose image this is; the image is what the API kept.</summary>
    public static string MascotOf(string mascotUrl) => AgentLooks.IsLook(mascotUrl) ? mascotUrl :
        AgentBuilderCopy.Mascots.FirstOrDefault(m => string.Equals(AgentBuilderCopy.MascotImage(m), mascotUrl, StringComparison.OrdinalIgnoreCase))
        ?? AgentBuilderCopy.Mascots.FirstOrDefault(m => string.Equals(m, mascotUrl, StringComparison.OrdinalIgnoreCase))
        ?? AgentBuilderCopy.Mascots[0];

    private static void RefuseOtherCurrency(AgentSettings settings)
    {
        if (settings.HandOverBigOrders && Parrot.SharedComponents.DesignSystem.CurrencyAmount.Code(settings.BigOrderCurrency) != "NGN")
            throw new ApiException(System.Net.HttpStatusCode.BadRequest, "Saving this handover currency needs an update to yára. Your change has not been saved.", "CurrencyNotSupported", []);
    }

    private static T Parse<T>(string? name, T fallback) where T : struct, Enum =>
        Enum.TryParse(name, true, out T value) ? value : fallback;
}
