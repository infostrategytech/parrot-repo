using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Conversations;

// Messenger's User Profile API (graph.facebook.com/{PSID}, with the Page token) and Instagram's
// (graph.instagram.com/{IGSID}, with the account's token): the webhook names the customer only by id.
// Each customer is asked for once a day per connection; a failure is remembered briefly, so a burst of
// messages from someone Meta will not describe costs one call, not one per message.
// A factory, not a typed client: the message worker holds this for its whole life.
public sealed class MetaContactNames(
    IHttpClientFactory httpClients,
    IDbContextFactory<ApplicationDbContext> contexts,
    IFacebookCredentialProtector facebookProtector,
    IInstagramCredentialProtector instagramProtector,
    IOptions<FacebookSettings> facebook,
    IOptions<InstagramSettings> instagram,
    IMemoryCache cache,
    ILogger<MetaContactNames> logger) : IContactNameLookup
{
    // The message is waiting on this before it is stored.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Found = TimeSpan.FromHours(24);
    private static readonly TimeSpan NotFound = TimeSpan.FromMinutes(10);

    public const string HttpClientName = "meta-contact-names";

    public async Task<string?> FindAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        if (message.Platform is not (MessagingPlatform.Facebook or MessagingPlatform.Instagram) || message.ConnectionId is not { } connection)
            return null;

        string key = $"contact-name:{connection}:{message.From}";
        if (cache.TryGetValue(key, out string? cached)) return cached;

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            string? name = message.Platform == MessagingPlatform.Facebook
                ? await FacebookAsync(connection, message.From, budget.Token)
                : await InstagramAsync(connection, message.From, budget.Token);
            cache.Set(key, name, name is null ? NotFound : Found);
            return name;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Never the customer's id: it is personal data, and the scope already names the message.
            logger.LogWarning("contact_name.lookup_failed Platform={Platform} Error={Error} Reason={Reason}",
                message.Platform, ex.GetType().Name, ex.Message);
            cache.Set(key, (string?)null, NotFound);
            return null;
        }
    }

    private async Task<string?> FacebookAsync(Guid connection, string psid, CancellationToken ct)
    {
        string? token = await TokenAsync(db => db.FacebookConnections
            .Where(c => c.Id == connection && c.DisabledAt == null).Select(c => c.AccessTokenEncrypted), ct);
        if (token is null) return null;

        using JsonDocument? profile = await GetAsync(
            $"https://graph.facebook.com/{Uri.EscapeDataString(facebook.Value.ApiVersion)}/{Uri.EscapeDataString(psid)}?fields=name,first_name,last_name",
            facebookProtector.Unprotect(token), "user_profile", ct);
        if (profile is null) return null;

        string? first = Text(profile, "first_name");
        string? last = Text(profile, "last_name");
        return Text(profile, "name") ?? (first is null && last is null ? null : $"{first} {last}".Trim());
    }

    private async Task<string?> InstagramAsync(Guid connection, string igsid, CancellationToken ct)
    {
        string? token = await TokenAsync(db => db.InstagramConnections
            .Where(c => c.Id == connection && c.DisabledAt == null).Select(c => c.AccessTokenEncrypted), ct);
        if (token is null) return null;

        using JsonDocument? profile = await GetAsync(
            InstagramGraph.Url(instagram, $"{Uri.EscapeDataString(igsid)}?fields=name,username"),
            instagramProtector.Unprotect(token), "instagram_profile", ct);

        // Many Instagram users leave their name empty; the handle is how the business knows them anyway.
        return profile is null ? null : Text(profile, "name") ?? (Text(profile, "username") is { } username ? "@" + username : null);
    }

    private async Task<string?> TokenAsync(Func<ApplicationDbContext, IQueryable<string?>> query, CancellationToken ct)
    {
        await using ApplicationDbContext db = await contexts.CreateDbContextAsync(ct);
        string? token = await query(db).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    // Null when Meta refuses (no permission, or the person can't be described); thrown on anything else.
    private async Task<JsonDocument?> GetAsync(string url, string accessToken, string endpoint, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        finally
        {
            MessageMetrics.MetaApiDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("endpoint", endpoint));
        }

        using HttpResponseMessage disposeResponse = response;
        if ((int)response.StatusCode >= 500) throw new HttpRequestException($"Meta's {endpoint} call failed ({(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("contact_name.refused Endpoint={Endpoint} MetaErrorCode={MetaErrorCode}",
                endpoint, await InstagramGraph.ErrorCodeAsync(response, ct) ?? "unknown");
            return null;
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string? Text(JsonDocument json, string property) =>
        json.RootElement.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
