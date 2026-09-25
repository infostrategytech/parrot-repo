using System.Text.Json.Serialization;

namespace Parrot.Web.Features.Dashboard.Channels.Models;

/// <summary>
/// Connecting WhatsApp, Facebook, Instagram and X to a workspace.
///
/// Ported from Aurora's Models/Channels/ChannelModels.cs. Nothing here goes
/// over the wire: Parrot has no channels workspace endpoint, so the workspace
/// is assembled in <c>ChannelsState</c> from Parrot's own integration calls.
/// </summary>

public enum ChannelId
{
    Whatsapp,
    Instagram,
    Facebook,
    /// <summary>Parrot's fourth channel. It has no Aurora counterpart and no backend.</summary>
    X
}

public enum ChannelStatus
{
    Connected,
    NotConnected,
    NeedsAttention
}

/// <summary>
/// Three ways in. <see cref="Embedded"/> is Meta Embedded Signup and becomes
/// the default once Tech Provider approval lands; <see cref="Assisted"/> only
/// exists because it has not.
/// </summary>
public enum ChannelSetupMode
{
    Guided,
    Assisted,
    Embedded
}

public enum ManualSetupStage
{
    Number,
    Credentials,
    Webhook,
    Complete
}

/// <summary>Every route a channel offers, across all three channels.</summary>
public enum ChannelPath
{
    /// <summary>WhatsApp: a fresh number.</summary>
    New,
    /// <summary>WhatsApp: keep the Business app on the same number.</summary>
    Coexistence,
    /// <summary>WhatsApp: bring a number from another provider.</summary>
    Migration,
    /// <summary>Instagram, without needing a Facebook Page — the requirement most SMEs fall down on.</summary>
    [JsonStringEnumMemberName("instagram-login")] InstagramLogin,
    [JsonStringEnumMemberName("facebook-login")] FacebookLogin,
    [JsonStringEnumMemberName("facebook-page")] FacebookPage
}

/// <summary>
/// What happens after Meta hands back the code. These are the three real
/// server-to-server calls, shown rather than hidden behind a spinner.
/// </summary>
public enum EmbeddedStage
{
    Idle,
    Popup,
    Linking,
    Registering,
    Subscribing,
    ReadyToTest,
    Connected,
    Failed
}

/// <summary>Connected means a message actually arrived, not that a form was filled in.</summary>
public enum ChannelVerification
{
    Idle,
    Waiting,
    Verified,
    Failed
}

public sealed record ChannelSetupState
{
    public ChannelSetupMode? Mode { get; init; }

    /// <summary>Kept on the server, so "Save and exit" survives a closed tab.</summary>
    public int Step { get; init; }

    public ManualSetupStage ManualStage { get; init; }

    public string PhoneNumberId { get; init; } = "";
    public string BusinessAccountId { get; init; } = "";

    /// <summary>Masked. The real token never comes back to the browser.</summary>
    public string? AccessTokenMask { get; init; }

    /// <summary>Null for a permanent System User token; a temporary one dies in a day.</summary>
    public DateTimeOffset? TokenExpiresAt { get; init; }

    public string PortfolioId { get; init; } = "";
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>The route chosen on the channel's first screen. Null until they pick.</summary>
    public ChannelPath? Path { get; init; }

    public EmbeddedStage Stage { get; init; }

    /// <summary>Why the popup or a follow-up call failed, in words an SME can act on.</summary>
    public string? Failure { get; init; }

    public ChannelVerification Verification { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
}

public sealed record BusinessChannel
{
    public ChannelId Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ChannelStatus Status { get; init; }
    public string? AccountName { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
    public string Requirement { get; init; } = "";

    /// <summary>How long setup usually takes, so nobody starts a 30-minute job by accident.</summary>
    public int GuidedMinutes { get; init; }

    public required ChannelSetupState Setup { get; init; }

    /// <summary>Set when a connection has stopped working, with what to do about it.</summary>
    public string? Attention { get; init; }
}

/// <summary>What the SME pastes into Meta: Parrot's WebhookUrl and VerifyToken for the connection.</summary>
public sealed record ChannelWebhook(string CallbackUrl, string VerifyToken);

public sealed record ChannelWorkspace
{
    public int ConnectedCount { get; init; }
    public IReadOnlyList<BusinessChannel> Channels { get; init; } = [];

    /// <summary>
    /// Parrot issues the callback URL and verify token per WhatsApp connection,
    /// so this is null until one exists.
    /// </summary>
    public ChannelWebhook? Webhook { get; init; }
}
