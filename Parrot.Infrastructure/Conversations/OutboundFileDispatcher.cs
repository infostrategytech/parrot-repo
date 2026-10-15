using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Conversations;

/// <summary>
/// Hands a stored reply's picture or file to WhatsApp or Messenger for the send worker. The file comes out of
/// private storage and the token from the connection row (decrypted with the API's key ring), so the job
/// carries neither. Whatever it cannot find or read is a result with a code, because nothing was sent; a call
/// to the channel that drops or times out throws, because then nobody can say whether it was.
/// </summary>
public sealed class OutboundFileDispatcher(
    IDbContextFactory<ApplicationDbContext> contexts,
    IWhatsAppSender whatsApp,
    IFacebookSender facebook,
    IWhatsAppTokenProtector whatsAppProtector,
    IFacebookCredentialProtector facebookProtector,
    IPrivateObjectStorage storage) : IOutboundFileDispatcher
{
    public async Task<FileDispatchResult> DispatchAsync(MessageDocument reply, CancellationToken cancellationToken)
    {
        MessageAttachment file = reply.Attachments![0];
        if (file.StorageKey is null || reply.ConnectionId is not { } connectionId || reply.OwnerUserId is null)
            return new FileDispatchResult(null, "file");

        // The channel's upload wants a body of known length, so the file is spooled to a temporary file (deleted when
        // closed) rather than held in memory: with several sends at once, whole files in memory add up.
        await using FileStream content = new(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (StoredObject? stored = await storage.GetAsync(file.StorageKey, cancellationToken))
        {
            if (stored is null) return new FileDispatchResult(null, "file");
            await stored.Content.CopyToAsync(content, cancellationToken);
        }

        string contentType = file.ContentType ?? "application/octet-stream";
        string fileName = file.FileName ?? "file";
        await using ApplicationDbContext db = await contexts.CreateDbContextAsync(cancellationToken);

        switch (reply.Platform)
        {
            case MessagingPlatform.WhatsApp:
            {
                WhatsAppConnection? connection = await db.WhatsAppConnections.AsNoTracking().SingleOrDefaultAsync(
                    c => c.Id == connectionId && c.OwnerUserId == reply.OwnerUserId && c.DisabledAt == null, cancellationToken);
                if (connection is null || string.IsNullOrWhiteSpace(connection.PhoneNumberId)) return new FileDispatchResult(null, "connection");
                string? token = Token(connection.AccessTokenEncrypted, whatsAppProtector.Unprotect);
                if (token is null) return new FileDispatchResult(null, "token");

                content.Position = 0;
                try
                {
                    // The message the agent quoted travels with the reply, as it does when the API sends it itself.
                    string id = reply.ReplyTo is { } quote
                        ? await whatsApp.SendMediaReplyAsync(connection.PhoneNumberId, token, reply.From, file.Kind, contentType, fileName, content, reply.TextBody, quote.ProviderMessageId, cancellationToken)
                        : await whatsApp.SendMediaAsync(connection.PhoneNumberId, token, reply.From, file.Kind, contentType, fileName, content, reply.TextBody, cancellationToken);
                    return new FileDispatchResult(id, null);
                }
                catch (WhatsAppRejectedException ex)
                {
                    return new FileDispatchResult(null, ex.MetaErrorCode ?? "unknown");
                }
            }

            case MessagingPlatform.Facebook:
            {
                FacebookConnection? page = await db.FacebookConnections.AsNoTracking().SingleOrDefaultAsync(
                    c => c.Id == connectionId && c.OwnerUserId == reply.OwnerUserId && c.DisabledAt == null, cancellationToken);
                if (page is null || string.IsNullOrWhiteSpace(page.PageId)) return new FileDispatchResult(null, "connection");
                string? token = Token(page.AccessTokenEncrypted, facebookProtector.Unprotect);
                if (token is null) return new FileDispatchResult(null, "token");

                content.Position = 0;
                try
                {
                    string id = reply.ReplyTo is { } quote
                        ? await facebook.SendMediaReplyAsync(page.PageId, token, reply.From, file.Kind, contentType, fileName, content, quote.ProviderMessageId, cancellationToken)
                        : await facebook.SendMediaAsync(page.PageId, token, reply.From, file.Kind, contentType, fileName, content, cancellationToken);
                    return new FileDispatchResult(id, null);
                }
                catch (FacebookRejectedException ex)
                {
                    return new FileDispatchResult(null, ex.MetaErrorCode ?? "unknown");
                }
            }

            default:
                return new FileDispatchResult(null, "channel");
        }
    }

    // Null when there is no token or the key ring cannot read it.
    private static string? Token(string? encrypted, Func<string, string> unprotect)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try
        {
            string token = unprotect(encrypted);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
