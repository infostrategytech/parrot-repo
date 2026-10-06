using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Events;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
    });
}

// The relay drains every tenant's outbox to Kafka and runs the in-repo consumers.
// It connects as catalog_outbox_relay and can see nothing but outbox and consumer_inbox.
builder.Services.AddCatalogRelay(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddHostedService<OutboxRelayService>();

CatalogOptions options = builder.Configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();
if (!string.Equals(options.Relay.Bus, "Null", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<CacheInvalidationConsumer>();
}

IHost host = builder.Build();
host.Run();
