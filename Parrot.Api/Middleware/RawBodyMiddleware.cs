namespace Parrot.Api.Middleware;

// Captures the raw request body for routes that need to validate HMAC signatures
// (e.g. Facebook X-Hub-Signature-256). Must run before the request body is consumed
// by model binding. Stored in HttpContext.Items["RawBody"].
public sealed class RawBodyMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Writes each provider webhook's raw body to the logs. Bodies carry what
    /// customers wrote and their phone numbers, so this is for diagnosing a
    /// provider's payloads only: turn it off (Webhooks__LogBodies=false) as
    /// soon as that is done. Read per request, so a config reload applies.
    /// </summary>
    public const string LogBodiesKey = "Webhooks:LogBodies";

    // Meta's webhooks are a few KB; this only stops a hostile body flooding the logs.
    private const int MaxLoggedBodyLength = 16 * 1024;

    private readonly ILogger<RawBodyMiddleware> _logger;

    private readonly IConfiguration _configuration;

    public RawBodyMiddleware(RequestDelegate next, ILogger<RawBodyMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Diagnostics cover every provider webhook, not just WhatsApp: a platform without them is
        // impossible to troubleshoot, because a payload rejected by model binding never reaches a
        // controller and so logs nothing about itself.
        string? platform = WebhookPlatform(context.Request.Path);

        if (platform is not null)
        {
            // Log arrival before reading/deserializing so invalid payloads are visible too.
            // Keep query values and headers out of logs; they may contain credentials. Presence alone
            // separates "the provider sent no signature" from "the signature did not match".
            _logger.LogInformation(
                "{Platform} webhook received. Method={Method} Path={Path} CorrelationId={CorrelationId} HasConnectionReference={HasConnectionReference} HasSignature={HasSignature} ContentLength={ContentLength}",
                platform,
                context.Request.Method,
                // TikTok's connection reference is the last path segment, so it stays out of the log.
                platform == "TikTok" ? "/api/webhooks/tiktok" : context.Request.Path.Value,
                context.Response.Headers["X-Correlation-ID"].ToString(),
                // Meta's callbacks carry ?ref=; TikTok's carry the reference as the last path segment.
                platform == "TikTok"
                    ? context.Request.Path.Value!.Length > "/api/webhooks/tiktok/".Length
                    : context.Request.Query.ContainsKey("ref"),
                context.Request.Headers.ContainsKey(platform == "TikTok" ? "TikTok-Signature" : "X-Hub-Signature-256"),
                context.Request.ContentLength);
        }

        if (context.Request.Path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();

            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
            string rawBody = await reader.ReadToEndAsync(context.RequestAborted);
            context.Items["RawBody"] = rawBody;

            context.Request.Body.Position = 0;

            // Before model binding, so a payload rejected with a 400 is logged too.
            if (platform is not null && _configuration.GetValue(LogBodiesKey, false))
            {
                _logger.LogInformation(
                    "{Platform} webhook body. CorrelationId={CorrelationId} Truncated={Truncated} Body={Body}",
                    platform,
                    context.Response.Headers["X-Correlation-ID"].ToString(),
                    rawBody.Length > MaxLoggedBodyLength,
                    rawBody.Length > MaxLoggedBodyLength ? rawBody[..MaxLoggedBodyLength] : rawBody);
            }
        }

        try
        {
            await _next(context);
            if (platform is not null)
            {
                _logger.Log(context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information,
                    "{Platform} webhook completed. CorrelationId={CorrelationId} StatusCode={StatusCode}",
                    platform, context.Response.Headers["X-Correlation-ID"].ToString(), context.Response.StatusCode);
            }
        }
        catch (Exception ex) when (platform is not null)
        {
            _logger.LogError(ex,
                "{Platform} webhook processing failed. CorrelationId={CorrelationId} Reason={Reason}",
                platform, context.Response.Headers["X-Correlation-ID"].ToString(), ex.Message);
            throw;
        }
    }

    // Null for any path that is not a provider webhook, which leaves those requests unlogged here.
    private static string? WebhookPlatform(PathString path) =>
        path.StartsWithSegments("/api/webhooks/whatsapp", StringComparison.OrdinalIgnoreCase) ? "WhatsApp"
        : path.StartsWithSegments("/api/webhooks/facebook", StringComparison.OrdinalIgnoreCase) ? "Facebook"
        : path.StartsWithSegments("/api/webhooks/tiktok", StringComparison.OrdinalIgnoreCase) ? "TikTok"
        : path.StartsWithSegments("/api/webhooks/instagram", StringComparison.OrdinalIgnoreCase) ? "Instagram"
        : null;
}
