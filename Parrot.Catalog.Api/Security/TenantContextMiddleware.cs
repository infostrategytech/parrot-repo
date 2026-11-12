using System.Security.Claims;
using Parrot.Catalog.Application.Tenancy;

namespace Parrot.Catalog.Api.Security;

/// <summary>
/// Copies tenant, actor and scopes from the verified principal into the scoped
/// <see cref="TenantContext"/>, which the database interceptor reads. Runs after
/// authentication and before authorization; it never reads the body.
/// </summary>
public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        string requestId = context.Response.Headers[RequestIdMiddleware.HeaderName].ToString();
        if (context.User.Identity?.IsAuthenticated == true && CatalogAuthentication.TenantIdOf(context.User) is { } tenantId)
        {
            string actor = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            string? owner = context.User.FindFirstValue(Parrot.Catalog.Application.Security.CatalogClaims.OwnerId);
            tenant.Establish(tenantId, actor, CatalogAuthentication.ScopesOf(context.User), requestId, owner is { Length: <= 256 } ? owner : null);
            using (_logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantId, ["Actor"] = actor }))
            {
                await _next(context);
            }

            return;
        }

        await _next(context);
    }
}

/// <summary>Assigns or echoes X-Request-ID and opens the structured log scope every line in this request carries.</summary>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        string requestId = incoming is { Length: > 0 and <= 128 } && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.NewGuid().ToString("D");
        context.Response.Headers[HeaderName] = requestId;
        context.TraceIdentifier = requestId;

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId,
            ["HttpMethod"] = context.Request.Method,
            ["RequestPath"] = context.Request.Path.Value,
        }))
        {
            await _next(context);
        }
    }
}
