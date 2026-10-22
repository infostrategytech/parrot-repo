using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// One place a customer messages the business: the channel, how they appear there, and its conversation
/// (a connection and a sender, as the Inbox's address uses them).
/// </summary>
public sealed record CustomerChannel(string Platform, string Handle, Guid ConnectionId, string Sender, string? AccountId = null, string? Username = null)
{
    public string ConversationId => $"{ConnectionId}:{Sender}";

    /// <summary>False for a channel added by hand that hasn't had a message yet: no conversation to open.</summary>
    public bool HasConversation => ConnectionId != Guid.Empty;
    public ChannelLink? PlatformLink => ChannelLinks.For(Platform, Sender, AccountId, Username);

    /// <summary>
    /// How someone appears on a channel, the same everywhere: @username on Instagram and TikTok, their name on
    /// Messenger, +number on WhatsApp; null when there is nothing better than an opaque id.
    /// </summary>
    public static string? HandleFor(string platform, string sender, string? username) => platform switch
    {
        "whatsapp" => sender.All(char.IsDigit) ? "+" + sender : sender,
        "facebook" => username is { Length: > 0 } name ? name : null,
        _ => username is { Length: > 0 } handle ? "@" + handle.TrimStart('@') : null,
    };
}

/// <summary>The latest internal note about a customer: who wrote it, when, and what it says.</summary>
public sealed record CustomerNote(string Author, DateTime At, string Text);

/// <summary>
/// Someone added by hand or imported: who did it ("You", or a teammate's name once there's a team) and when.
/// </summary>
public sealed record CustomerAdded(string By, DateTime At);

/// <summary>
/// A customer as Customer details shows them (designer, 21 October 2026: one panel for the Inbox and the
/// Customers page). The Inbox makes it from the open conversation; Customers from the customers API. The
/// first channel is the one the panel is about: the open conversation, or where they wrote last.
/// </summary>
public sealed record CustomerView(
    string Seed,
    string Name,
    string Initials,
    IReadOnlyList<CustomerChannel> Channels,
    string? Email = null,
    string? Phone = null,
    string? Location = null,
    DateTime? LastMessageAt = null,
    DateTime? ReplyWindowEndsAt = null,
    CustomerNote? LatestNote = null,
    string MediaKey = "",
    CustomerAdded? Added = null)
{
    /// <summary>The channel the panel is about; null for someone added with no channel.</summary>
    public CustomerChannel? Primary => Channels.Count > 0 ? Channels[0] : null;

    /// <summary>The open conversation's customer, as the Inbox knows them.</summary>
    public static CustomerView From(ConversationItem conversation)
    {
        var note = conversation.Messages
            .Where(message => message.Kind == MessageKind.Note)
            .OrderByDescending(message => message.ReceivedAt)
            .FirstOrDefault();
        var handle = CustomerChannel.HandleFor(conversation.Channel, conversation.Sender, conversation.Username) ?? conversation.Sender;
        return new CustomerView(
            conversation.Id,
            conversation.CustomerName,
            conversation.CustomerInitials,
            [new CustomerChannel(conversation.Channel, handle, conversation.ConnectionId, conversation.Sender, conversation.AccountId, conversation.Username)],
            conversation.Customer?.Email,
            // WhatsApp's sender is the customer's number; on TikTok and Facebook it is an opaque id, not a phone.
            conversation.Customer?.Phone ?? (conversation.Channel == "whatsapp" ? conversation.Sender : null),
            conversation.Customer?.Location,
            conversation.UpdatedAt,
            conversation.ReplyWindowEndsAt,
            note is null ? null : new CustomerNote(note.Author?.Name ?? "Team member", note.ReceivedAt, note.Text),
            // Files that finish loading change the media without changing how many messages carry files.
            $"{conversation.Id}:{conversation.Messages.Count(m => m.Attachments.Count > 0)}:{conversation.Messages.Sum(m => m.Attachments.Count(a => a.Status == "ready"))}");
    }
}
