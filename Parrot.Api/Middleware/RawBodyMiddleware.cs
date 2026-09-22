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
        if (context.Request.Path.StartsWithSegments("/api/webhooks/whatsapp", StringComparison.OrdinalIgnoreCase))
        {
            // Log arrival before reading/deserializing so invalid payloads are visible too.
            // Keep query values and headers out of logs; they may contain credentials.
            _logger.LogInformation(
                "WhatsApp webhook received. Method={Method} Path={Path} CorrelationId={CorrelationId} HasConnectionReference={HasConnectionReference} ContentLength={ContentLength}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.Headers["X-Correlation-ID"].ToString(),
                context.Request.Query.ContainsKey("ref"),
                context.Request.ContentLength);
        }

        if (context.Request.Path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();

            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
            context.Items["RawBody"] = await reader.ReadToEndAsync(context.RequestAborted);

            context.Request.Body.Position = 0;

            if (context.Request.Path.StartsWithSegments("/api/webhooks/whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                // Temporary full-payload diagnostics, explicitly enabled for webhook troubleshooting.
                // Includes customer message content; remove when troubleshooting is complete.
                _logger.LogInformation(
                    "WhatsApp webhook raw payload. CorrelationId={CorrelationId} Payload={Payload}",
                    context.Response.Headers["X-Correlation-ID"].ToString(),
                    context.Items["RawBody"]);
            }
        }

        bool isWhatsApp = context.Request.Path.StartsWithSegments("/api/webhooks/whatsapp", StringComparison.OrdinalIgnoreCase);
        try
        {
            await _next(context);
            if (isWhatsApp)
            {
                _logger.Log(context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information,
                    "WhatsApp webhook completed. CorrelationId={CorrelationId} StatusCode={StatusCode}",
                    context.Response.Headers["X-Correlation-ID"].ToString(), context.Response.StatusCode);
            }
        }
        catch (Exception ex) when (isWhatsApp)
        {
            _logger.LogError(ex,
                "WhatsApp webhook processing failed. CorrelationId={CorrelationId} Reason={Reason}",
                context.Response.Headers["X-Correlation-ID"].ToString(), ex.Message);
            throw;
        }
    }
}
