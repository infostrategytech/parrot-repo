namespace Parrot.Web.Components.Pages.Dashboard.Contacts;

public sealed record ContactIdentity(Guid ConnectionId, string Platform, string Sender, string? Username);

public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string ChannelIdentifier { get; set; } = string.Empty;
    public string? ChannelPlatform { get; set; }
    public bool HasWhatsApp { get; set; }
    public bool HasInstagram { get; set; }
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string InstagramHandle { get; set; } = string.Empty;
    public string LastInteraction { get; set; } = "—";
    public DateTime? LastSeenAt { get; set; }
    public string LastInteractionChannel { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int OrderCount { get; set; }
    public string Stage { get; set; } = string.Empty;
    public List<ContactOrder> Orders { get; set; } = new();
    public List<ContactIdentity> Identities { get; set; } = [];

    /// <summary>The conversation they last wrote in (the first one when that is not known): the one Open conversation goes to.</summary>
    public ContactIdentity? Primary =>
        Identities.FirstOrDefault(identity => identity.Platform == LastInteractionChannel) ?? Identities.FirstOrDefault();

    /// <summary>Their conversations as the inbox and tasks name them: "{connection}:{sender}".</summary>
    public IReadOnlyList<string> ConversationIds => [.. Identities.Select(identity => $"{identity.ConnectionId}:{identity.Sender}")];
}
