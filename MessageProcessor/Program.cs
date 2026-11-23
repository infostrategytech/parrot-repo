using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MessageProcessor;
using MessageProcessor.Monitoring;
using OpenTelemetry.Metrics;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.BusinessSources;
using Parrot.Application.Conversations;
using Parrot.Application.Customers;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Application.Utils;
using Parrot.Infrastructure.Agents;
using Parrot.Infrastructure.BusinessSources;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Customers;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.MessageBus;
using Parrot.Infrastructure.Messaging;
using Parrot.Infrastructure.SocialMediaIntegrations;
using Parrot.Infrastructure.Storage;

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
builder.Services.AddSingleton<ICustomerRepository, EfCustomerRepository>();
builder.Services.AddSingleton<ITenantIdLookup, EfTenantIdLookup>();

// Agent channel lookup: asks the AI service whether an agent is live on a channel,
// so messages.persisted carries UserToAgent (not UserToTenant) when the AI should reply.
// Requires JwtSettings (to mint service tokens) and OmniAi:BaseUrl.
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<OmniAiSettings>(builder.Configuration.GetSection(OmniAiSettings.SectionName));
OmniAiSettings omniAiSettings = builder.Configuration.GetSection(OmniAiSettings.SectionName).Get<OmniAiSettings>()
    ?? new OmniAiSettings();
builder.Services.AddSingleton<IJwtHelper, JwtHelper>();
builder.Services.AddSingleton<IServiceTokenFactory, ServiceTokenFactory>();
builder.Services.AddHttpClient("AgentOrchestrator", client =>
{
    if (Uri.TryCreate(omniAiSettings.BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUrl))
        client.BaseAddress = baseUrl;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, omniAiSettings.TimeoutSeconds));
});
builder.Services.AddSingleton<IAgentOrchestratorClient>(sp => new AgentOrchestratorClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("AgentOrchestrator"),
    sp.GetRequiredService<IServiceTokenFactory>(),
    sp.GetRequiredService<IOptions<OmniAiSettings>>(),
    sp.GetRequiredService<ILogger<AgentOrchestratorClient>>()));
builder.Services.AddSingleton<IAgentChannelLookup, OmniAiAgentChannelLookup>();
builder.Services.AddSingleton<IHumanTakeoverStore, MongoHumanTakeoverStore>();

// Messenger and Instagram webhooks name the customer only by id; the worker asks Meta for the name
// with the connection's token, so it reads the API's key ring (never writing to it).
builder.Services.Configure<FacebookSettings>(builder.Configuration.GetSection(FacebookSettings.SectionName));
builder.Services.Configure<InstagramSettings>(builder.Configuration.GetSection(InstagramSettings.SectionName));
builder.Services.AddWhatsAppCredentialProtection(builder.Configuration, builder.Environment.IsDevelopment(), readOnly: true);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(MetaContactNames.HttpClientName).RemoveAllLoggers();
builder.Services.AddSingleton<IContactNameLookup, MetaContactNames>();

// Customers' pictures and files are copied to private storage as the message arrives, because
// WhatsApp's media links expire within minutes and need the connection's token.
builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection(StorageSettings.SectionName));
builder.Services.AddSingleton<IPrivateObjectStorage, S3PrivateObjectStorage>();
builder.Services.Configure<WhatsAppSettings>(builder.Configuration.GetSection(WhatsAppSettings.SectionName));
builder.Services.AddHttpClient(MetaMediaDownloader.HttpClientName).RemoveAllLoggers();
builder.Services.AddSingleton<IInboundMediaStore, MetaMediaDownloader>();

builder.Services.AddSingleton<IMessageDocumentMapper, WhatsAppMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, FacebookMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, TikTokMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, InstagramMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapperFactory, MessageDocumentMapperFactory>();

builder.Services.AddSingleton<IInboundMessageDecoder, WhatsAppInboundDecoder>();
builder.Services.AddSingleton<IInboundMessageDecoder, FacebookInboundDecoder>();
builder.Services.AddSingleton<IInboundMessageDecoder, TikTokInboundDecoder>();
builder.Services.AddSingleton<IInboundMessageDecoder, InstagramInboundDecoder>();
builder.Services.AddSingleton<InboundMessageDecoders>();

// Its constructor creates the collection's indexes, including the unique
// MessageId one that every duplicate check relies on.
builder.Services.AddSingleton<MongoMessageRepository>();
builder.Services.AddSingleton<IStoredMessageStore, MongoStoredMessageStore>();
builder.Services.AddSingleton<IConversationSequence, MongoConversationSequence>();

builder.Services.AddSingleton<IMessageBus, KafkaMessageBus>();
builder.Services.AddSingleton<IMessagePersistedPublisher, KafkaMessagePersistedPublisher>();
builder.Services.AddSingleton<IMediaJobPublisher, KafkaMediaJobPublisher>();
builder.Services.AddSingleton<IOutboundJobPublisher, KafkaOutboundJobPublisher>();
builder.Services.AddSingleton<IMessageSettledPublisher, KafkaMessageSettledPublisher>();
// The week-long download links each file in MessagePersisted carries, for the AI team.
builder.Services.AddSingleton<IMediaLinkSigner, MediaLinkSigner>();
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<InboundMessageProcessor>(
    sp, sp.GetRequiredService<IOptions<StorageSettings>>().Value.DeferInboundMedia));
builder.Services.AddSingleton<MediaJobProcessor>(sp => ActivatorUtilities.CreateInstance<MediaJobProcessor>(sp));
builder.Services.AddSingleton<IDeliveryReceipts, MongoDeliveryReceipts>();
builder.Services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
builder.Services.AddHttpClient<IFacebookSender, FacebookSender>();
builder.Services.AddHttpClient<IInstagramSender, InstagramSender>();
builder.Services.AddSingleton<IOutboundFileDispatcher>(sp => ActivatorUtilities.CreateInstance<OutboundFileDispatcher>(sp));
builder.Services.AddSingleton<OutboundFileProcessor>(sp => ActivatorUtilities.CreateInstance<OutboundFileProcessor>(sp));

// Services required by AiReplyWorker: Omni-AI sends a text reply event; Parrot routes
// it to the right channel and publishes MessagePersisted(AgentToUser). ConversationService
// is scoped so its EF-based connection repositories each get a fresh DbContext per event.
builder.Services.AddSingleton<IConversationStore, MongoConversationStore>();
builder.Services.AddScoped<ApplicationDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddScoped<IWhatsAppConnectionRepository, WhatsAppConnectionRepository>();
builder.Services.AddScoped<IFacebookConnectionRepository, FacebookConnectionRepository>();
builder.Services.AddScoped<IInstagramConnectionRepository, InstagramConnectionRepository>();
builder.Services.AddScoped(typeof(IAppLogger<>), typeof(AppLogger<>));
builder.Services.AddScoped<ConversationService>();
builder.Services.AddSingleton<AiReplyProcessor>();
// The product photos the AI sends with a reply are downloaded here, then sent like a teammate's picture.
builder.Services.AddHttpClient<IAgentPhotoFetcher, Parrot.Infrastructure.Messaging.HttpAgentPhotoFetcher>(client =>
    {
        client.Timeout = Parrot.Infrastructure.Messaging.HttpAgentPhotoFetcher.Timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Parrot-CRM");
    })
    .ConfigurePrimaryHttpMessageHandler(Parrot.Infrastructure.Messaging.HttpAgentPhotoFetcher.Handler)
    .RemoveAllLoggers();

// Business knowledge: profile metadata fetched by BusinessKnowledgeWorker and published
// to Kafka by SourcesOutboxWorker. All three services share the same scoped DbContext.
builder.Services.AddScoped<IBusinessSourceRepository, BusinessSourceRepository>();
builder.Services.AddScoped<ISnapshotPublisher, SnapshotPublisher>();
builder.Services.AddSingleton<IWhatsAppProfileClient, WhatsAppProfileClient>();
builder.Services.AddHttpClient(WhatsAppProfileClient.HttpClientName, client =>
    client.BaseAddress = new Uri("https://graph.facebook.com/")).RemoveAllLoggers();
builder.Services.AddSingleton<IInstagramProfileClient, InstagramProfileClient>();
builder.Services.AddHttpClient(InstagramProfileClient.HttpClientName, client =>
    client.BaseAddress = new Uri("https://graph.facebook.com/")).RemoveAllLoggers();
builder.Services.AddSingleton<IFacebookPageClient, FacebookPageClient>();
builder.Services.AddHttpClient(FacebookPageClient.HttpClientName, client =>
    client.BaseAddress = new Uri("https://graph.facebook.com/")).RemoveAllLoggers();

// One program, four jobs: Worker:Roles says which this process does, so each can be deployed and scaled by itself.
WorkerRoles roles = WorkerRoleSettings.Parse(builder.Configuration[WorkerRoleSettings.Key]);
builder.Services.AddSingleton(typeof(WorkerRoles), roles);
if (roles.HasFlag(WorkerRoles.Messages)) builder.Services.AddHostedService<Worker>();
if (roles.HasFlag(WorkerRoles.Media)) builder.Services.AddHostedService<MediaWorker>();
if (roles.HasFlag(WorkerRoles.Outbound)) builder.Services.AddHostedService<OutboundWorker>();
if (roles.HasFlag(WorkerRoles.AiReply)) builder.Services.AddHostedService<AiReplyWorker>();
if (roles.HasFlag(WorkerRoles.SourcesOutbox)) builder.Services.AddHostedService<SourcesOutboxWorker>();
if (roles.HasFlag(WorkerRoles.BusinessKnowledge)) builder.Services.AddHostedService<BusinessKnowledgeWorker>();
if (roles.HasFlag(WorkerRoles.Media | WorkerRoles.Outbound))
{
    builder.Services.AddSingleton<PendingSweeper>(sp => ActivatorUtilities.CreateInstance<PendingSweeper>(sp));
    builder.Services.AddHostedService<PendingSweeperService>();
}

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

// A job this service doesn't run logs nothing at all, so say once which ones it does.
static string OnOff(WorkerRoles roles, WorkerRoles role) => roles.HasFlag(role) ? "on" : "off";
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MessageProcessor.WorkerRoles").LogInformation(
    "worker.roles Configured={Configured} Messages={Messages} Media={Media} Outbound={Outbound} AiReplies={AiReplies} " +
    "SourcesOutbox={SourcesOutbox} BusinessKnowledge={BusinessKnowledge}",
    builder.Configuration[WorkerRoleSettings.Key] ?? "(not set, so all)",
    OnOff(roles, WorkerRoles.Messages), OnOff(roles, WorkerRoles.Media), OnOff(roles, WorkerRoles.Outbound),
    OnOff(roles, WorkerRoles.AiReply), OnOff(roles, WorkerRoles.SourcesOutbox), OnOff(roles, WorkerRoles.BusinessKnowledge));

// The meter provider must exist first: measurements made before it are dropped.
host.Services.GetRequiredService<MeterProvider>();
MessageMetrics.StartAtZero();

host.Run();
