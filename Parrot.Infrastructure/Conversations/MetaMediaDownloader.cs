using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Conversations;

/// <summary>
/// Saves the files of an incoming message to private storage. WhatsApp hands over a media id: the file is
/// found with the connection's token (Graph API, then the short-lived address it returns, with the same
/// token). Messenger and Instagram hand over the file's address on Meta's CDN, which needs no token but is
/// checked against Meta's own hosts first, because the address comes out of a webhook body.
///
/// A factory, not a typed client: the message worker holds this for its whole life.
/// </summary>
public sealed class MetaMediaDownloader(
    IHttpClientFactory httpClients,
    IDbContextFactory<ApplicationDbContext> contexts,
    IWhatsAppTokenProtector whatsAppProtector,
    IPrivateObjectStorage storage,
    IOptions<WhatsAppSettings> whatsApp,
    IOptions<StorageSettings> storageSettings,
    ILogger<MetaMediaDownloader> logger) : IInboundMediaStore
{
    public const string HttpClientName = "meta-media";

    // Each file may take this long in total; the message is waiting on it before it is stored.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // Where Meta serves media from. Anything else in a webhook body is refused, never fetched.
    private static readonly string[] MetaHosts = ["fbcdn.net", "cdninstagram.com", "fbsbx.com", "facebook.com", "instagram.com"];

    public async Task<IReadOnlyList<IncomingMedia>> SaveAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        List<IncomingMedia> saved = [];
        for (int index = 0; index < (message.Media?.Count ?? 0); index++)
        {
            IncomingMedia media = message.Media![index];
            if (media.Kind == MediaKinds.Link) { saved.Add(media); continue; }

            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);
            try
            {
                saved.Add(await SaveOneAsync(message, media, index, budget.Token) ?? media);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Never the address: it is signed and names the customer's file. The reason is what tells a missing
                // bucket or credential from a Meta refusal, so it is kept.
                logger.LogWarning("media.download_failed Kind={Kind} Platform={Platform} Error={Error} Reason={Reason}",
                    media.Kind, message.Platform, ex.GetType().Name, ex.Message);
                saved.Add(media with { SourceUrl = null });
            }
        }

        return saved;
    }

    private async Task<IncomingMedia?> SaveOneAsync(IncomingMessage message, IncomingMedia media, int index, CancellationToken ct)
    {
        using HttpResponseMessage response = await OpenAsync(message, media, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("media.refused Kind={Kind} Platform={Platform} Status={Status}", media.Kind, message.Platform, (int)response.StatusCode);
            return media with { SourceUrl = null };
        }

        long limit = storageSettings.Value.MaxInboundMediaBytes;
        if (response.Content.Headers.ContentLength > limit)
            return media with { SourceUrl = null, SizeBytes = response.Content.Headers.ContentLength };

        // Buffered: S3 needs the length up front, and the cap bounds the memory.
        await using MemoryStream bytes = new();
        await using (Stream body = await response.Content.ReadAsStreamAsync(ct))
        {
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (bytes.Length + read > limit) return media with { SourceUrl = null };
                bytes.Write(chunk, 0, read);
            }
        }
        if (bytes.Length == 0) return media with { SourceUrl = null };

        string contentType = media.ContentType
            ?? response.Content.Headers.ContentType?.MediaType
            ?? "application/octet-stream";
        bytes.Position = 0;
        string key = Key(message, index, contentType, media.FileName);
        await storage.PutAsync(key, bytes, contentType, ct);

        return media with
        {
            Kind = MediaKinds.Refine(media.Kind, contentType),
            ContentType = contentType,
            SizeBytes = bytes.Length,
            StorageKey = key,
            // The link was only the way in; it expires and is not kept.
            SourceUrl = null,
        };
    }

    private async Task<HttpResponseMessage> OpenAsync(IncomingMessage message, IncomingMedia media, CancellationToken ct)
    {
        HttpClient http = httpClients.CreateClient(HttpClientName);
        if (message.Platform != MessagingPlatform.WhatsApp)
        {
            Uri uri = ChecksOut(media.SourceUrl) ?? throw new InvalidOperationException("The media address is not one of Meta's.");
            return await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        string token = await WhatsAppTokenAsync(message, ct);
        string id = media.ProviderMediaId ?? throw new InvalidOperationException("The WhatsApp media has no id.");

        // Step one: the id's current address, MIME type and size.
        using HttpRequestMessage lookup = new(HttpMethod.Get,
            $"https://graph.facebook.com/{Uri.EscapeDataString(whatsApp.Value.ApiVersion)}/{Uri.EscapeDataString(id)}");
        lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage described = await http.SendAsync(lookup, ct);
        if (!described.IsSuccessStatusCode) return new HttpResponseMessage(described.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await described.Content.ReadAsStringAsync(ct));
        Uri download = (json.RootElement.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String ? ChecksOut(url.GetString()) : null)
            ?? throw new InvalidOperationException("WhatsApp returned no usable media address.");

        // Step two: the bytes, with the same token.
        HttpRequestMessage fetch = new(HttpMethod.Get, download);
        fetch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(fetch, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<string> WhatsAppTokenAsync(IncomingMessage message, CancellationToken ct)
    {
        Guid connection = message.ConnectionId ?? throw new InvalidOperationException("The message has no connection.");
        await using ApplicationDbContext db = await contexts.CreateDbContextAsync(ct);
        string? token = await db.WhatsAppConnections
            .Where(c => c.Id == connection && c.DisabledAt == null).Select(c => c.AccessTokenEncrypted).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("The connection has no access token.")
            : whatsAppProtector.Unprotect(token);
    }

    // HTTPS on one of Meta's hosts, or nothing.
    private static Uri? ChecksOut(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
        && MetaHosts.Any(host => uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;

    // Built from the message's id and the file's position, never from anything the customer chose: the key
    // can neither collide with another customer's nor climb out of its prefix.
    private static string Key(IncomingMessage message, int index, string contentType, string? fileName)
    {
        string owner = new(message.OwnerUserId?.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray() ?? []);
        string message_ = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.MessageId))).ToLowerInvariant()[..32];
        return $"customer-media/{(owner.Length == 0 ? "unknown" : owner)}/{message_}-{index}{Extension(contentType, fileName)}";
    }

    private static string Extension(string contentType, string? fileName)
    {
        string? extension = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/3gpp" => ".3gp",
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/aac" => ".aac",
            "audio/mp4" => ".m4a",
            "audio/amr" => ".amr",
            "application/pdf" => ".pdf",
            _ => null,
        };
        if (extension is not null) return extension;

        // An extension the customer's file name carries, kept only if it is plain letters and digits.
        string ext = Path.GetExtension(fileName ?? "");
        return ext.Length is > 1 and <= 8 && ext[1..].All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : "";
    }
}
