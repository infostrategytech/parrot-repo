namespace Parrot.Application.Email;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);

    /// <summary>
    /// The same email with a plain-text version alongside the HTML, for
    /// text-only clients and spam filters, which score HTML-only mail lower.
    /// A sender that cannot send both sends the HTML alone.
    /// </summary>
    Task SendEmailAsync(string toEmail, string subject, string htmlBody, string textBody) =>
        SendEmailAsync(toEmail, subject, htmlBody);
}
