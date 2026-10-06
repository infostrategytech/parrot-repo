using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Parrot.Catalog.Api.Middleware;
using Parrot.Catalog.Api.OpenApi;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Catalog.Infrastructure.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Structured logs everywhere but the developer console; scopes carry request and tenant ids.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
    });
}

// Imports go through S3; nothing larger than 1 MB is accepted here.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Limits.MaxBodyBytes);

builder.Services.AddCatalogInfrastructure(builder.Configuration);
CatalogOptions catalogOptions = builder.Configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();
if (catalogOptions.Relay.InProcess)
{
    builder.Services.AddCatalogRelay(builder.Configuration, builder.Environment.IsDevelopment());
    builder.Services.AddHostedService<OutboxRelayService>();
    if (!string.Equals(catalogOptions.Relay.Bus, "Null", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddHostedService<CacheInvalidationConsumer>();
    }
}

CatalogAuthOptions authOptions = CatalogAuthentication.ResolveAuthOptions(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(authOptions);
builder.Services.AddCatalogAuthentication(authOptions);
builder.Services.AddCatalogRateLimiting(builder.Configuration);
builder.Services.AddScoped<ReadCacheFilter>();
builder.Services.AddScoped<WriteInvalidationFilter>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers(options =>
    {
        options.Filters.Add(new ProducesAttribute("application/json"));
        options.Filters.AddService<WriteInvalidationFilter>();
    })
    .AddJsonOptions(options =>
    {
        JsonSerializerOptions json = options.JsonSerializerOptions;
        json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        json.DictionaryKeyPolicy = null;
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        // Strict schemas: a field the contract does not know is a 400, not silently dropped.
        json.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        json.MaxDepth = 16;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            List<string> errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .SelectMany(e => e.Value!.Errors.Select(err => string.IsNullOrEmpty(e.Key) ? Clean(err) : $"{e.Key}: {Clean(err)}"))
                .ToList();
            return new BadRequestObjectResult(new ErrorResponse
            {
                ErrorCode = "VALIDATION_ERROR",
                Message = "The request is not valid.",
                Errors = errors,
                TraceId = context.HttpContext.TraceIdentifier,
            });

            static string Clean(Microsoft.AspNetCore.Mvc.ModelBinding.ModelError err)
                => string.IsNullOrEmpty(err.ErrorMessage) ? "The value could not be read." : err.ErrorMessage;
        };
    });

builder.Services.AddCatalogOpenApi();

// Telemetry: traces for HTTP and Postgres, metrics for HTTP and the catalog's own meter.
string? otlpEndpoint = builder.Configuration["OpenTelemetry:Endpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("parrot-catalog-api", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
         .AddHttpClientInstrumentation()
         .AddSource("Npgsql");
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation().AddMeter(CatalogMetrics.MeterName);
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    });

WebApplication app = builder.Build();

if (catalogOptions.ApplyMigrations)
{
    if (string.IsNullOrWhiteSpace(catalogOptions.ConnectionStrings.Migrator))
    {
        throw new InvalidOperationException("Catalog:ApplyMigrations is on but Catalog:ConnectionStrings:Migrator is not set.");
    }

    DbContextOptions<CatalogDbContext> migratorOptions = new DbContextOptionsBuilder<CatalogDbContext>()
        .UseNpgsql(catalogOptions.ConnectionStrings.Migrator, n => n.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
        .Options;
    await using CatalogDbContext migrator = new CatalogDbContext(migratorOptions);
    await migrator.Database.MigrateAsync();
    app.Logger.LogInformation("Catalog migrations applied.");
}

app.UseForwardedHeaders();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapGet("/health", async (CatalogReadDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous()
    .DisableRateLimiting();
app.MapControllers();

app.Run();

/// <summary>Exposed for WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
