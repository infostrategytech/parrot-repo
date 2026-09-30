using MessageProcessor;
using Parrot.Infrastructure.MessageBus;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

builder.Services.AddSingleton<IMessageBus, KafkaMessageBus>();

var host = builder.Build();
host.Run();
