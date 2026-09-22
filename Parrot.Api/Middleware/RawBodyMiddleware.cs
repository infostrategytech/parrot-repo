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
            // Do not log query values, headers or bodies: they may contain credentials or customer content.
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
        }

        await _next(context);
    }
}
