using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Caching;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Infrastructure;

namespace Parrot.Catalog.Api.Middleware;

/// <summary>
/// ETag / If-None-Match plus the Redis read cache for published reads. The key is the
/// tenant (from the token), the route and a hash of query string and body; responses
/// with <c>as_of</c> are never cached because they are not "now".
/// </summary>
public sealed class ReadCacheFilter : IAsyncActionFilter
{
    private readonly ICatalogReadCache _cache;
    private readonly ITenantContext _tenant;
    private readonly CatalogMetrics _metrics;
    private readonly JsonSerializerOptions _json;
    private readonly TimeProvider _clock;

    public ReadCacheFilter(ICatalogReadCache cache, ITenantContext tenant, CatalogMetrics metrics, IOptions<JsonOptions> json, TimeProvider clock)
    {
        _cache = cache;
        _tenant = tenant;
        _metrics = metrics;
        _json = json.Value.JsonSerializerOptions;
        _clock = clock;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        HttpContext http = context.HttpContext;
        if (!_tenant.IsSet)
        {
            await next();
            return;
        }

        string resource = context.ActionDescriptor.AttributeRouteInfo?.Template ?? http.Request.Path.Value ?? "/";
        bool cacheable = !http.Request.Query.ContainsKey("as_of") && !http.Request.Query.ContainsKey("asOf");
        string requestHash = Hash(http.Request.Method, http.Request.Path.Value, http.Request.QueryString.Value, context.ActionArguments);

        if (cacheable)
        {
            CachedResponse? cached = await _cache.GetAsync(_tenant.TenantId, resource, requestHash, http.RequestAborted);
            if (cached is not null)
            {
                _metrics.CacheHit(resource);
                http.Response.Headers.ETag = cached.ETag;
                http.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
                http.Response.Headers["X-Cache"] = "HIT";
                if (Matches(http, cached.ETag))
                {
                    context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
                    return;
                }

                context.Result = new FileContentResult(cached.Body, "application/json");
                return;
            }

            _metrics.CacheMiss(resource);
        }

        ActionExecutedContext executed = await next();
        if (executed.Exception is not null || executed.Result is not ObjectResult { Value: not null } result || (result.StatusCode ?? 200) != 200)
        {
            return;
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result.Value, result.Value.GetType(), _json);
        string etag = $"\"{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()[..32]}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        http.Response.Headers["X-Cache"] = cacheable ? "MISS" : "BYPASS";

        if (cacheable)
        {
            await _cache.SetAsync(_tenant.TenantId, resource, requestHash, new CachedResponse(body, etag, _clock.GetUtcNow().UtcDateTime), http.RequestAborted);
        }

        if (Matches(http, etag))
        {
            executed.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            return;
        }

        executed.Result = new FileContentResult(body, "application/json");
    }

    private static bool Matches(HttpContext http, string etag)
    {
        string? header = http.Request.Headers.IfNoneMatch.FirstOrDefault();
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        return header.Split(',').Select(h => h.Trim()).Any(h => h == "*" || h == etag || h == "W/" + etag);
    }

    private string Hash(string method, string? path, string? query, IDictionary<string, object?> arguments)
    {
        StringBuilder sb = new StringBuilder(method).Append(' ').Append(path).Append(query);
        foreach ((string key, object? value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            // Only bound request data takes part in the key; framework arguments (the cancellation token) do not.
            if (value is CancellationToken)
            {
                continue;
            }

            sb.Append('|').Append(key).Append('=').Append(value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), _json));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
