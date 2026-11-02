using Microsoft.AspNetCore.Mvc.Filters;
using Parrot.Catalog.Application.Caching;
using Parrot.Catalog.Application.Tenancy;

namespace Parrot.Catalog.Api.Middleware;

/// <summary>
/// After a successful write (POST/PUT/DELETE) the tenant's cache generation is bumped
/// right away, so a reader on the shared Redis sees its own writes without waiting for
/// the relay. The event-driven invalidator (ADR-006) still runs for writes made by other
/// processes, such as the tools CLI.
/// </summary>
public sealed class WriteInvalidationFilter : IAsyncActionFilter
{
    // The routes that change catalog data: the write API and the dashboard's items view of it.
    private static readonly string[] WritePaths = ["/api/catalog/admin", "/api/catalog/items", "/api/catalog/categories"];

    private readonly ICatalogReadCache _cache;
    private readonly ITenantContext _tenant;

    public WriteInvalidationFilter(ICatalogReadCache cache, ITenantContext tenant)
    {
        _cache = cache;
        _tenant = tenant;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ActionExecutedContext executed = await next();
        if (executed.Exception is null && _tenant.IsSet && !HttpMethods.IsGet(context.HttpContext.Request.Method)
            && WritePaths.Any(path => context.HttpContext.Request.Path.StartsWithSegments(path)))
        {
            await _cache.BumpGenerationAsync(_tenant.TenantId, context.HttpContext.RequestAborted);
        }
    }
}
