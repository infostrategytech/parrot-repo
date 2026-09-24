using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Messaging;
using Parrot.Infrastructure.Data;
using WhatsAppMessageIngestor;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaSettings>(
    builder.Configuration.GetSection(KafkaSettings.SectionName));

builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection(MongoDbSettings.SectionName));

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    MongoDbSettings settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
    return new MongoClient(settings.ConnectionString);
});

string postgresConnection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is required to record WhatsApp message receipts. Use the same PostgreSQL database as the API.");
if (string.IsNullOrWhiteSpace(postgresConnection))
    throw new InvalidOperationException("ConnectionStrings__DefaultConnection must not be empty.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(postgresConnection));
builder.Services.AddSingleton<IConnectionMessageReceipt, ConnectionMessageReceipt>();
builder.Services.AddSingleton<IMessageDocumentMapper, WhatsAppMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, FacebookMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapperFactory, MessageDocumentMapperFactory>();
builder.Services.AddSingleton<MongoMessageRepository>();
builder.Services.AddSingleton<IMessageRepository>(sp => new ConnectionTrackingMessageRepository(
    sp.GetRequiredService<MongoMessageRepository>(), sp.GetRequiredService<IConnectionMessageReceipt>()));

builder.Services.AddSingleton<IPlatformMessageHandler, WhatsAppMessageHandler>();
builder.Services.AddSingleton<IPlatformMessageHandler, FacebookMessageHandler>();
builder.Services.AddSingleton<IPlatformMessageHandlerFactory, PlatformMessageHandlerFactory>();
builder.Services.AddSingleton<IPlatformMessageService, PlatformMessageService>();

// Registers every "_t" discriminator before the consumer reads anything from the collection.
Parrot.Domain.Entities.Messages.MessageSerializationRegistry.Register();

builder.Services.AddHostedService<KafkaConsumerWorker>();

IHost host = builder.Build();

// Resolved eagerly so a platform missing its mapper fails the boot, not the first message of that kind.
host.Services.GetRequiredService<IMessageDocumentMapperFactory>();

host.Run();
