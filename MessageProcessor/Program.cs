using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MessageProcessor;
using MessageProcessor.Monitoring;
using OpenTelemetry.Metrics;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.MessageBus;
using Parrot.Infrastructure.Messaging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// One line per entry, UTC, with scopes and the trace id, so one message's
// journey can be followed across services by its trace id.
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});
builder.Logging.Configure(options =>
    options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

// Room to finish and commit the batch in hand (KeyedSubscriptionOptions.ShutdownGrace is 25 s).
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Services.Configure<KafkaSettings>(builder.Configuration.GetSection(KafkaSettings.SectionName));
builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection(MongoDbSettings.SectionName));

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<MongoDbSettings>>().Value.ConnectionString));

string postgresConnection = builder.Configuration.GetConnectionString("DefaultConnection") is { Length: > 0 } connection
    ? connection
    : throw new InvalidOperationException("ConnectionStrings__DefaultConnection is required to record connection receipts. Use the same PostgreSQL database as the API.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(postgresConnection));
builder.Services.AddSingleton<IConnectionMessageReceipt, ConnectionMessageReceipt>();

builder.Services.AddSingleton<IMessageDocumentMapper, WhatsAppMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, FacebookMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, TikTokMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapperFactory, MessageDocumentMapperFactory>();

builder.Services.AddSingleton<IInboundMessageDecoder, WhatsAppInboundDecoder>();
builder.Services.AddSingleton<IInboundMessageDecoder, FacebookInboundDecoder>();
builder.Services.AddSingleton<IInboundMessageDecoder, TikTokInboundDecoder>();
builder.Services.AddSingleton<InboundMessageDecoders>();

// Its constructor creates the collection's indexes, including the unique
// MessageId one that every duplicate check relies on.
builder.Services.AddSingleton<MongoMessageRepository>();
builder.Services.AddSingleton<IStoredMessageStore, MongoStoredMessageStore>();
builder.Services.AddSingleton<IConversationSequence, MongoConversationSequence>();

builder.Services.AddSingleton<IMessageBus, KafkaMessageBus>();
builder.Services.AddSingleton<IMessagePersistedPublisher, KafkaMessagePersistedPublisher>();
builder.Services.AddSingleton<InboundMessageProcessor>();
builder.Services.AddHostedService<Worker>();

// The Parrot.Messaging meter on http://<host>:<Monitoring:MetricsPort>/metrics for
// Prometheus. The worker has no web server, so the exporter brings its own
// listener; keep the port on the internal network only.
int metricsPort = builder.Configuration.GetValue("Monitoring:MetricsPort", 9464);
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter(MessageMetrics.MeterName)
    .AddPrometheusHttpListener(options => options.UriPrefixes = [$"http://+:{metricsPort}/"]));
builder.Services.AddHostedService<QueueGauges>();

// Registers every "_t" discriminator before anything reads the collection.
Parrot.Domain.Entities.Messages.MessageSerializationRegistry.Register();

IHost host = builder.Build();

// Resolved eagerly: a platform without a mapper, or indexes that can't be
// created, fail the boot rather than the first message.
host.Services.GetRequiredService<IMessageDocumentMapperFactory>();
host.Services.GetRequiredService<MongoMessageRepository>();

// The meter provider must exist first: measurements made before it are dropped.
host.Services.GetRequiredService<MeterProvider>();
MessageMetrics.StartAtZero();

host.Run();
