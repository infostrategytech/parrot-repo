namespace Parrot.Web.Services;

/// <summary>A message sent from the public Contact page (designer, 23 November 2026). Frontend contract only: the
/// backend has no endpoint that receives website messages yet (see the website backend request).</summary>
public sealed record ContactMessage(string Name, string? Business, string Email, string Subject, string Message)
{
    public const int MaxNameLength = 120;
    public const int MaxSubjectLength = 160;
    public const int MaxMessageLength = 5000;
}

public interface IContactMessages
{
    /// <summary>Sends the message to the yára team. Throws when it couldn't be sent.</summary>
    Task SendAsync(ContactMessage message, CancellationToken cancellationToken);
}

/// <summary>The live site, until the backend can receive website messages: nothing is sent, and the page says so
/// and offers the email address instead. No invented delivery.</summary>
public sealed class UnavailableContactMessages : IContactMessages
{
    public Task SendAsync(ContactMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Website messages can't be sent yet.");
}
