using Parrot.Web.Features.Dashboard.Channels.Models;

namespace Parrot.Web.Features.Dashboard.Channels.State;

/// <summary>
/// One of Parrot's four channels, with Parrot's own copy and icon.
/// </summary>
/// <param name="Key">Parrot's channel key ("whatsapp", "facebook", "instagram", "twitter"), kept for the state-only calls.</param>
/// <param name="Requirement">What the old pre-setup checklist said you need, shown on the card's meta line.</param>
/// <param name="GuidedMinutes">How long the old checklist said setup takes. Zero when Parrot never said.</param>
public sealed record ChannelInfo(
    ChannelId Id,
    string Key,
    string Name,
    string Description,
    string IconPath,
    string Requirement,
    int GuidedMinutes);

/// <summary>
/// The Channels workspace, assembled on the client.
///
/// Aurora reads a <see cref="ChannelWorkspace"/> from one endpoint and every
/// write answers with a fresh copy. Parrot has no such endpoint: WhatsApp and
/// Facebook each report their own status, and Instagram and X are state-only.
/// This class holds those pieces and builds the workspace Aurora's screens
/// bind to, so the page code can stay shaped like Aurora's.
///
/// Setup progress (route chosen, guided step, manual stage) lives here too,
/// because Parrot has no endpoint to save it to. It survives navigating
/// around the app within one circuit, and a fresh circuit resumes from what
/// the backend knows about the connection.
/// </summary>
public class ChannelsState
{
    /// <summary>Parrot's copy for a WhatsApp connection the backend has switched off.</summary>
    public const string DisabledAttention = "This connection is disabled. Contact support to continue.";

    /// <summary>Parrot's copy for a TikTok connection whose access has lapsed.</summary>
    public const string TikTokReauthorizeAttention = "TikTok access has expired. Authorize TikTok again to keep replying.";

    public bool IsLoading { get; private set; }

    public IReadOnlyList<ChannelInfo> Channels { get; } =
    [
        // Names, descriptions and what you need are Aurora's copy (the design).
        new(ChannelId.Whatsapp, "whatsapp", "WhatsApp",
            "Reply to customers using your business number.",
            "assets/images/icons/whatsapp.png",
            "A Facebook account", 25),
        new(ChannelId.Instagram, "instagram", "Instagram",
            "Bring Instagram DMs into your OmniAI inbox.",
            "assets/images/icons/instagram.png",
            "An Instagram professional account", 5),
        new(ChannelId.Facebook, "facebook", "Facebook",
            "Manage messages sent to your Facebook Page.",
            "assets/images/icons/facebook.png",
            "Admin access to a Facebook Page", 8),
        new(ChannelId.TikTok, "tiktok", "TikTok",
            "Reply to the messages sent to your TikTok business account.",
            "",
            "A TikTok Business Account and your own TikTok developer app", 10),
        new(ChannelId.X, "twitter", "X",
            "Bring X DMs into your OmniAI inbox.",
            "",
            "An X account with DMs open", 5),
    ];

    /// <summary>The WhatsApp connection the page works with: the live one, or the disabled one if that is all there is.</summary>
    public WhatsAppConnectionInfo? WhatsAppConnection { get; private set; }

    /// <summary>The connection plus what Meta needs pasted in. Null until the setup has been loaded or created.</summary>
    public WhatsAppSetup? WhatsAppSetup { get; private set; }

    public FacebookWebhookSetup? FacebookSetup { get; private set; }

    public TikTokSetup? TikTokSetup { get; private set; }

    public InstagramWebhookSetup? InstagramSetup { get; private set; }

    /// <summary>Instagram and X are connected in state only, exactly as before.</summary>
    private readonly Dictionary<ChannelId, (string Account, string Date)> _stateOnly = new();

    /// <summary>A backend-reported connection the person has disconnected in state only (Facebook).</summary>
    private readonly HashSet<ChannelId> _dismissed = [];

    /// <summary>Setup progress per channel. Missing means "never touched in this circuit".</summary>
    private readonly Dictionary<ChannelId, ChannelSetupState> _setup = new();

    /// <summary>A WhatsApp message the gRPC check confirmed, ahead of the status call catching up.</summary>
    private bool _whatsAppVerified;

    public void SetLoading(bool value) => IsLoading = value;

    public void SetWhatsApp(WhatsAppConnectionInfo? connection)
    {
        WhatsAppConnection = connection;
        if (connection is null) { WhatsAppSetup = null; _whatsAppVerified = false; }
        else if (WhatsAppSetup is not null && WhatsAppSetup.Connection.Id == connection.Id)
            WhatsAppSetup = WhatsAppSetup with { Connection = connection };
        else if (WhatsAppSetup is not null) WhatsAppSetup = null;
    }

    public void SetWhatsAppSetup(WhatsAppSetup setup)
    {
        WhatsAppSetup = setup;
        WhatsAppConnection = setup.Connection;
    }

    // A message that arrived is the proof of a connection, so an earlier
    // disconnect no longer stands (the demo can connect WhatsApp again).
    public void MarkWhatsAppVerified()
    {
        _whatsAppVerified = true;
        _dismissed.Remove(ChannelId.Whatsapp);
    }

    public void SetFacebook(FacebookWebhookSetup? setup)
    {
        FacebookSetup = setup;
        if (setup is not null) _dismissed.Remove(ChannelId.Facebook);
    }

    public void SetInstagram(InstagramWebhookSetup? setup)
    {
        InstagramSetup = setup;
        if (setup is not null) _dismissed.Remove(ChannelId.Instagram);
    }

    public void SetTikTok(TikTokSetup? setup)
    {
        TikTokSetup = setup;
        if (setup is not null) _dismissed.Remove(ChannelId.TikTok);
    }

    public ChannelSetupState Setup(ChannelId id) =>
        _setup.TryGetValue(id, out var state) ? state : Seed(id);

    public void SaveSetup(ChannelId id, ChannelSetupState state) => _setup[id] = state;

    /// <summary>
    /// Start over. Only the local progress goes — Parrot's backend keeps the
    /// connection, so the route choice comes back but nothing typed into Meta
    /// is lost.
    /// </summary>
    public void ResetSetup(ChannelId id) => _setup[id] = new ChannelSetupState();

    public void ConnectChannel(string key, string account, string date)
    {
        var channel = Channels.FirstOrDefault(c => c.Key == key);
        if (channel is null) return;
        _stateOnly[channel.Id] = (account, date);
        _dismissed.Remove(channel.Id);
        _setup.Remove(channel.Id);
    }

    public void DisconnectChannel(string key)
    {
        var channel = Channels.FirstOrDefault(c => c.Key == key);
        if (channel is null) return;
        _stateOnly.Remove(channel.Id);
        _dismissed.Add(channel.Id);
        _setup.Remove(channel.Id);
        if (channel.Id == ChannelId.Whatsapp) _whatsAppVerified = false;
    }

    public ChannelWorkspace Workspace
    {
        get
        {
            var channels = Channels.Select(Build).ToList();
            return new ChannelWorkspace
            {
                Channels = channels,
                ConnectedCount = channels.Count(c => c.Status == ChannelStatus.Connected),
                Webhook = WhatsAppSetup is { } setup ? new ChannelWebhook(setup.WebhookUrl, setup.VerifyToken) : null
            };
        }
    }

    // Facebook connected by its window (demo mode) is held in state, like
    // Instagram and X; otherwise it is what the API's setup says.
    private BusinessChannel Build(ChannelInfo info) => info.Id switch
    {
        ChannelId.Whatsapp => BuildWhatsApp(info),
        ChannelId.Facebook when FacebookSetup is not null && !_stateOnly.ContainsKey(ChannelId.Facebook) => BuildFacebook(info),
        ChannelId.TikTok => BuildTikTok(info),
        ChannelId.Instagram when InstagramSetup is not null && !_stateOnly.ContainsKey(ChannelId.Instagram) => BuildInstagram(info),
        _ => BuildStateOnly(info)
    };

    private BusinessChannel BuildWhatsApp(ChannelInfo info)
    {
        var connection = WhatsAppConnection;
        var disabled = connection?.DisabledAt is not null;
        var connected = connection is not null && !disabled && !_dismissed.Contains(info.Id)
            && (connection.SetupStatus == "receiving-configured" || connection.FirstMessageReceivedAt is not null || _whatsAppVerified);

        return new BusinessChannel
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Requirement = info.Requirement,
            GuidedMinutes = info.GuidedMinutes,
            Status = connected ? ChannelStatus.Connected : disabled ? ChannelStatus.NeedsAttention : ChannelStatus.NotConnected,
            AccountName = connection?.DisplayPhoneNumber ?? connection?.PhoneNumberId,
            ConnectedAt = connection?.FirstMessageReceivedAt,
            Attention = disabled ? DisabledAttention : null,
            // The local record stays even once connected: the embedded flow's
            // success screen is the Connected stage of that record, and the
            // card never offers setup actions on a connected channel anyway.
            Setup = Setup(info.Id)
        };
    }

    private BusinessChannel BuildFacebook(ChannelInfo info)
    {
        var setup = FacebookSetup;
        var connected = setup is not null && setup.WebhookVerified && setup.HasAccessToken
            && !string.IsNullOrEmpty(setup.PageId) && !_dismissed.Contains(info.Id);

        return new BusinessChannel
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Requirement = info.Requirement,
            GuidedMinutes = info.GuidedMinutes,
            Status = connected ? ChannelStatus.Connected : ChannelStatus.NotConnected,
            AccountName = setup?.PageName,
            Setup = connected ? new ChannelSetupState() : Setup(info.Id)
        };
    }

    private BusinessChannel BuildInstagram(ChannelInfo info)
    {
        var setup = InstagramSetup;
        var connected = setup is not null && setup.WebhookVerified && setup.HasAccessToken
            && !string.IsNullOrEmpty(setup.AccountId) && !_dismissed.Contains(info.Id);

        return new BusinessChannel
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Requirement = info.Requirement,
            GuidedMinutes = info.GuidedMinutes,
            Status = connected ? ChannelStatus.Connected : ChannelStatus.NotConnected,
            AccountName = string.IsNullOrEmpty(setup?.Username) ? null : "@" + setup.Username,
            Setup = connected ? new ChannelSetupState() : Setup(info.Id)
        };
    }

    // TikTok is only ever what the API's setup says: connected once the owner has authorized the account,
    // and needing attention when TikTok access has lapsed and they must authorize again.
    private BusinessChannel BuildTikTok(ChannelInfo info)
    {
        var setup = TikTokSetup;
        var authorized = setup is { Authorized: true } && !_dismissed.Contains(info.Id);
        var lapsed = authorized && setup!.NeedsReauthorization;

        return new BusinessChannel
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Requirement = info.Requirement,
            GuidedMinutes = info.GuidedMinutes,
            Status = lapsed ? ChannelStatus.NeedsAttention : authorized ? ChannelStatus.Connected : ChannelStatus.NotConnected,
            AccountName = authorized ? setup!.DisplayName : null,
            Attention = lapsed ? TikTokReauthorizeAttention : null,
            Setup = authorized && !lapsed ? new ChannelSetupState() : Setup(info.Id)
        };
    }

    private BusinessChannel BuildStateOnly(ChannelInfo info)
    {
        var connected = _stateOnly.TryGetValue(info.Id, out var account);

        return new BusinessChannel
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Requirement = info.Requirement,
            GuidedMinutes = info.GuidedMinutes,
            Status = connected ? ChannelStatus.Connected : ChannelStatus.NotConnected,
            AccountName = connected ? account.Account : null,
            ConnectedAt = connected && DateTimeOffset.TryParse(account.Date, out var at) ? at : null,
            // Kept once connected, as for WhatsApp: the demo flow's success
            // screen is the Connected stage of this record.
            Setup = Setup(info.Id)
        };
    }

    /// <summary>
    /// Where a setup nobody has touched in this circuit stands, read off the
    /// backend. Aurora gets this from the server; Parrot's WhatsApp and
    /// Facebook status calls carry enough to put a reopened setup back where
    /// it stopped.
    /// </summary>
    private ChannelSetupState Seed(ChannelId id)
    {
        if (id == ChannelId.Whatsapp && WhatsAppConnection is { DisabledAt: null } connection)
            return SeedWhatsApp(connection);

        if (id == ChannelId.Facebook && FacebookSetup is { } facebook
            && (facebook.WebhookVerified || facebook.HasAccessToken))
        {
            return new ChannelSetupState
            {
                Mode = ChannelSetupMode.Guided,
                ManualStage = facebook.WebhookVerified ? ManualSetupStage.Credentials : ManualSetupStage.Number
            };
        }

        if (id == ChannelId.Instagram && InstagramSetup is { } instagram
            && (instagram.WebhookVerified || instagram.HasAccessToken))
        {
            return new ChannelSetupState
            {
                Mode = ChannelSetupMode.Guided,
                ManualStage = instagram.WebhookVerified ? ManualSetupStage.Credentials : ManualSetupStage.Number
            };
        }

        return new ChannelSetupState();
    }

    /// <summary>
    /// The manual path, read off what the backend already holds: IDs and a
    /// token mean the credentials section is done; a verified webhook means
    /// only the message test is left.
    /// </summary>
    public static ChannelSetupState SeedWhatsApp(WhatsAppConnectionInfo connection)
    {
        var hasAccount = !string.IsNullOrEmpty(connection.WhatsAppBusinessAccountId)
            && !string.IsNullOrEmpty(connection.PhoneNumberId);
        var verified = connection.WebhookVerifiedAt is not null;

        var stage = verified && hasAccount ? ManualSetupStage.Complete
            : hasAccount && connection.HasAccessToken ? ManualSetupStage.Webhook
            : ManualSetupStage.Number;

        return new ChannelSetupState
        {
            Mode = ChannelSetupMode.Guided,
            Step = stage == ManualSetupStage.Complete ? GuidedSteps.TestStep : GuidedSteps.ConnectionStep,
            ManualStage = stage,
            PhoneNumberId = connection.PhoneNumberId ?? "",
            BusinessAccountId = connection.WhatsAppBusinessAccountId ?? ""
        };
    }
}
