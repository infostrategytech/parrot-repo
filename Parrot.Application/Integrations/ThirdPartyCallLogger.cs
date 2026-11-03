using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Parrot.Application.Integrations;

/// <summary>
/// One log line for every call to an outside service, under the "third_party." events: which
/// service, host, method and path, the status and how long it took. Only the path is written: the
/// query, headers and request bodies can carry tokens or what a person said, so they never reach the log.
/// A refused call also says why: the service's errorCode, message and traceId (its own logs are
/// searchable by it), and for a 401 or 403 what the token claimed (issuer, audience, expiry and
/// whether it names a tenant and workspace), never the token or its signature.
/// </summary>
/// <remarks>
/// A stream returns at its headers, so its time is the wait for the first byte, not the whole read.
/// </remarks>
public sealed class ThirdPartyCallLogger(string service, ILogger<ThirdPartyCallLogger> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string method = request.Method.Method;
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        string host = request.RequestUri?.Authority ?? string.Empty;
        long started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "third_party.call Service={Service} Host={Host} Method={Method} Path={Path} Status={Status} ElapsedMs={ElapsedMs}",
                    service,
                    host,
                    method,
                    path,
                    status,
                    ElapsedMs(started));
                return response;
            }

            long elapsed = ElapsedMs(started);
            (string errorCode, string message, string traceId) = await RefusalAsync(response, cancellationToken);
            string challenge = response.Headers.WwwAuthenticate.ToString();
            if (status is 401 or 403)
            {
                TokenClaims token = TokenClaims.Of(request);
                logger.LogWarning(
                    "third_party.call Service={Service} Host={Host} Method={Method} Path={Path} Status={Status} ElapsedMs={ElapsedMs} " +
                    "ErrorCode={ErrorCode} Message={Message} TraceId={TraceId} Challenge={Challenge} " +
                    "TokenSent={TokenSent} TokenIssuer={TokenIssuer} TokenAudience={TokenAudience} TokenExpiresInS={TokenExpiresInS} " +
                    "TokenHasTenant={TokenHasTenant} TokenHasWorkspace={TokenHasWorkspace}",
                    service,
                    host,
                    method,
                    path,
                    status,
                    elapsed,
                    errorCode,
                    message,
                    traceId,
                    challenge,
                    token.Sent,
                    token.Issuer,
                    token.Audience,
                    token.ExpiresInSeconds,
                    token.HasTenant,
                    token.HasWorkspace);
            }
            else
            {
                logger.LogWarning(
                    "third_party.call Service={Service} Host={Host} Method={Method} Path={Path} Status={Status} ElapsedMs={ElapsedMs} " +
                    "ErrorCode={ErrorCode} Message={Message} TraceId={TraceId} RetryAfter={RetryAfter}",
                    service,
                    host,
                    method,
                    path,
                    status,
                    elapsed,
                    errorCode,
                    message,
                    traceId,
                    response.Headers.RetryAfter?.ToString() ?? string.Empty);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "third_party.cancelled Service={Service} Host={Host} Method={Method} Path={Path} ElapsedMs={ElapsedMs}",
                service,
                host,
                method,
                path,
                ElapsedMs(started));
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable, refused or timed out: the type and message say which, never a body. The
            // innermost error is the real reason ("see inner exception" says nothing on its own).
            Exception cause = ex;
            while (cause.InnerException is not null) cause = cause.InnerException;
            logger.LogWarning(
                "third_party.failed Service={Service} Host={Host} Method={Method} Path={Path} ElapsedMs={ElapsedMs} Error={Error}: {Reason} Cause={Cause}: {CauseReason}",
                service,
                host,
                method,
                path,
                ElapsedMs(started),
                ex.GetType().Name,
                ex.Message,
                cause.GetType().Name,
                cause.Message);
            throw;
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // A refusal's reason, from the error envelope ({errorCode, message, traceId}, also in snake case) or
    // the first of a body that is not one. Buffered, so whoever called still reads the body.
    private static async Task<(string ErrorCode, string Message, string TraceId)> RefusalAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int MaxShown = 300;
        try
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body)) return ("", "(empty body)", "");
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                JsonElement root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.Object)
                    root = detail;
                return (Field(root, "errorCode", "error_code", "code"), Shorten(Field(root, "message", "detail", "title"), MaxShown), Field(root, "traceId", "trace_id"));
            }
            catch (JsonException)
            {
                return ("", Shorten(body, MaxShown), "");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return ("", $"(body unreadable: {ex.GetType().Name})", "");
        }
    }

    private static string Field(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value))
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        }

        return "";
    }

    private static string Shorten(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    /// <summary>What the bearer token said about itself, read from its payload without checking it.</summary>
    private sealed record TokenClaims(bool Sent, string Issuer, string Audience, long? ExpiresInSeconds, bool HasTenant, bool HasWorkspace)
    {
        private static readonly TokenClaims None = new(false, "", "", null, false, false);

        public static TokenClaims Of(HttpRequestMessage request)
        {
            if (request.Headers.Authorization is not { Scheme: var scheme, Parameter: { } token }
                || !scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                return None;
            string[] parts = token.Split('.');
            if (parts.Length != 3) return None with { Sent = true, Issuer = "(not a JWT)" };
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
                using JsonDocument json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                JsonElement claims = json.RootElement;
                long? expiresIn = claims.TryGetProperty("exp", out JsonElement exp) && exp.TryGetInt64(out long seconds)
                    ? seconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : null;
                return new TokenClaims(
                    true,
                    Field(claims, "iss"),
                    Field(claims, "aud"),
                    expiresIn,
                    !string.IsNullOrEmpty(Field(claims, "tenant_id")),
                    !string.IsNullOrEmpty(Field(claims, "workspace_id")));
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                return None with { Sent = true, Issuer = "(unreadable payload)" };
            }
        }
    }
}
