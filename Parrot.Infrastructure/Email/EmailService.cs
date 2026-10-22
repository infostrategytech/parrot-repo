using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Parrot.Application.Email;

namespace Parrot.Infrastructure.Email;

public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;

    public EmailService(IOptions<EmailSettings> settings)
    {
        _settings = settings.Value;
    }

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody) =>
        SendAsync(toEmail, subject, new TextPart("html") { Text = htmlBody });

    // multipart/alternative: clients show the HTML, and fall back to the text.
    public Task SendEmailAsync(string toEmail, string subject, string htmlBody, string textBody) =>
        SendAsync(toEmail, subject, new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody());

    private async Task SendAsync(string toEmail, string subject, MimeEntity body)
    {
        MimeMessage message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = body;

        using SmtpClient client = new SmtpClient();
        SecureSocketOptions socketOptions = _settings.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, socketOptions);

        if (_settings.RequiresAuthentication)
        {
            await client.AuthenticateAsync(_settings.SmtpUser, _settings.SmtpPassword);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
