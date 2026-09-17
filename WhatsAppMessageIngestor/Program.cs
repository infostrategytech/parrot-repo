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

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<IConnectionMessageReceipt, ConnectionMessageReceipt>();
builder.Services.AddSingleton<MongoMessageRepository>();
builder.Services.AddSingleton<IMessageRepository>(sp => new ConnectionTrackingMessageRepository(
    sp.GetRequiredService<MongoMessageRepository>(), sp.GetRequiredService<IConnectionMessageReceipt>()));

builder.Services.AddSingleton<IPlatformMessageHandler, WhatsAppMessageHandler>();
builder.Services.AddSingleton<IPlatformMessageHandlerFactory, PlatformMessageHandlerFactory>();
builder.Services.AddSingleton<IPlatformMessageService, PlatformMessageService>();

builder.Services.AddHostedService<KafkaConsumerWorker>();

IHost host = builder.Build();
host.Run();
