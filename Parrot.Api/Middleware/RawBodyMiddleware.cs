namespace Parrot.Api.Middleware;

// Captures the raw request body for routes that need to validate HMAC signatures
// (e.g. Facebook X-Hub-Signature-256). Must run before the request body is consumed
// by model binding. Stored in HttpContext.Items["RawBody"].
public sealed class RawBodyMiddleware
{
    private readonly RequestDelegate _next;

    private readonly ILogger<RawBodyMiddleware> _logger;

    public RawBodyMiddleware(RequestDelegate next, ILogger<RawBodyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
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
                context.Request.Path.Value,
                context.Response.Headers["X-Correlation-ID"].ToString(),
                context.Request.Query.ContainsKey("ref"),
                context.Request.Headers.ContainsKey("X-Hub-Signature-256"),
                context.Request.ContentLength);
        }

        if (context.Request.Path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();

            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
            context.Items["RawBody"] = await reader.ReadToEndAsync(context.RequestAborted);

            context.Request.Body.Position = 0;
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
        : null;
}
