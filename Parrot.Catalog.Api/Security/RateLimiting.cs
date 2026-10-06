using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Middleware;

namespace Parrot.Catalog.Api.Security;

/// <summary>
/// Per tenant and per token. Search and AI tool endpoints get tighter windows; an
/// unauthenticated caller is bucketed by address. Limits are per instance.
/// </summary>
public static class RateLimiting
{
    public const string Read = "catalog-read";
    public const string Search = "catalog-search";
    public const string Write = "catalog-write";
    public const string Ai = "catalog-ai";

    public static IServiceCollection AddCatalogRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        int readPerMinute = configuration.GetValue("Catalog:RateLimits:ReadPerMinute", 600);
        int searchPerMinute = configuration.GetValue("Catalog:RateLimits:SearchPerMinute", 120);
        int writePerMinute = configuration.GetValue("Catalog:RateLimits:WritePerMinute", 300);
        int aiPerMinute = configuration.GetValue("Catalog:RateLimits:AiPerMinute", 60);
        int tenantPerMinute = configuration.GetValue("Catalog:RateLimits:TenantPerMinute", 3000);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.ContentType = "application/json";
                ErrorResponse body = new ErrorResponse { ErrorCode = "RATE_LIMITED", Message = "Too many requests. Wait a moment, then try again.", TraceId = ctx.HttpContext.TraceIdentifier };
                await ctx.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), ct);
            };

            // One bucket per tenant across every endpoint, on top of the per-token policies below.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(TenantKey(ctx), _ => Window(tenantPerMinute)));

            options.AddPolicy(Read, ctx => RateLimitPartition.GetFixedWindowLimiter(TokenKey(ctx), _ => Window(readPerMinute)));
            options.AddPolicy(Search, ctx => RateLimitPartition.GetFixedWindowLimiter(TokenKey(ctx), _ => Window(searchPerMinute)));
            options.AddPolicy(Write, ctx => RateLimitPartition.GetFixedWindowLimiter(TokenKey(ctx), _ => Window(writePerMinute)));
            options.AddPolicy(Ai, ctx => RateLimitPartition.GetFixedWindowLimiter(TokenKey(ctx), _ => Window(aiPerMinute)));
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };

    private static string TenantKey(HttpContext ctx)
        => CatalogAuthentication.TenantIdOf(ctx.User)?.ToString("N") ?? $"ip:{ctx.Connection.RemoteIpAddress}";

    private static string TokenKey(HttpContext ctx)
    {
        string? jti = ctx.User.FindFirstValue("jti");
        string? sub = ctx.User.FindFirstValue("sub");
        string tenant = TenantKey(ctx);
        return jti is not null ? $"{tenant}:jti:{jti}" : sub is not null ? $"{tenant}:sub:{sub}" : tenant;
    }
}
