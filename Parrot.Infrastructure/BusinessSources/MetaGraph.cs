using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Parrot.Application.Logging;

namespace Parrot.Infrastructure.BusinessSources;

/// <summary>
/// A Graph API read with the access token in the Authorization header, never in the URL, where
/// proxies, access logs and error messages keep it. A refusal comes back with Meta's own reason.
/// </summary>
internal static partial class MetaGraph
{
    /// <summary>Meta's error body: { "error": { "message", "type", "code", "error_subcode", "fbtrace_id" } }.</summary>
    internal sealed record Refusal(int Status, string? Code, string? Subcode, string? Type, string? Message, string? FbTraceId);

    public static async Task<(T? Body, Refusal? Refused)> GetAsync<T>(HttpClient http, string path, string accessToken, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(ct), null);
        return (default, await RefusalAsync(response, accessToken, ct));
    }

    /// <summary>One line for any refused read, so "meta.read_refused" finds them all; FbTraceId is what Meta support asks for.</summary>
    public static void LogRefused(ILogger logger, string read, string externalId, string apiVersion, Refusal refusal) =>
        logger.LogWarning(
            "meta.read_refused Read={Read} ExternalId={ExternalId} ApiVersion={ApiVersion} Status={Status} MetaCode={MetaCode} " +
            "MetaSubcode={MetaSubcode} MetaType={MetaType} FbTraceId={FbTraceId} MetaMessage={MetaMessage}",
            read, externalId, apiVersion, refusal.Status, refusal.Code, refusal.Subcode, refusal.Type, refusal.FbTraceId, refusal.Message);

    private static async Task<Refusal> RefusalAsync(HttpResponseMessage response, string accessToken, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        // Meta repeats a token it can't read in its own message ("Malformed access token EAAG…"): never let it reach a log.
        string body = Scrub(await response.Content.ReadAsStringAsync(ct), accessToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                return new Refusal(status, Field(error, "code"), Field(error, "error_subcode"), Field(error, "type"),
                    Field(error, "message"), Field(error, "fbtrace_id"));
        }
        catch (JsonException)
        {
            // Not Meta's error shape (a proxy's page, say): the start of it says enough.
        }

        return new Refusal(status, null, null, null, LogMask.Text(body, 200), null);
    }

    private static string? Field(JsonElement error, string name) =>
        !error.TryGetProperty(name, out JsonElement value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : value.GetRawText();

    private static string Scrub(string text, string accessToken) =>
        MetaToken().Replace(accessToken.Length > 0 ? text.Replace(accessToken, "***", StringComparison.Ordinal) : text, "***");

    // Meta's user, page and Instagram tokens, should another one be quoted back.
    [GeneratedRegex(@"\b(?:EAA|IGQV|IGAA)[A-Za-z0-9_\-]{8,}")]
    private static partial Regex MetaToken();
}
