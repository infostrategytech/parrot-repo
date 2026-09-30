using System.Text.Json;
using Confluent.Kafka;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.MessageBus;

public sealed class KafkaMessageBus : IMessageBus, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly KafkaSettings _settings;
    private readonly IProducer<string?, string> _producer;
    private readonly ILogger<KafkaMessageBus> _logger;

    public KafkaMessageBus(IOptions<KafkaSettings> settings, ILogger<KafkaMessageBus> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        ProducerConfig producerConfig = new ProducerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            Acks = Acks.Leader,
        };
        ApplyGcpAuth(producerConfig);

        ProducerBuilder<string?, string> producerBuilder = new ProducerBuilder<string?, string>(producerConfig);

        if (_settings.UseGcpAuth)
        {
            producerBuilder.SetOAuthBearerTokenRefreshHandler((producer, _) => RefreshGcpToken(producer));
        }

        _producer = producerBuilder.Build();
    }

    public async Task PublishAsync<TMessage>(string topic, string? key, TMessage message, CancellationToken cancellationToken = default)
    {
        await _producer.ProduceAsync(
            topic,
            new Message<string?, string>
            {
                Key = key,
                Value = JsonSerializer.Serialize(message),
            },
            cancellationToken);
    }

    public Task SubscribeAsync<TMessage>(string topic, Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
        // Consume() blocks the calling thread, so the loop gets a thread of its own.
        => Task.Run(() => ConsumeLoopAsync(topic, handler, cancellationToken), CancellationToken.None);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }

    private async Task ConsumeLoopAsync<TMessage>(string topic, Func<TMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        ConsumerConfig consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            GroupId = _settings.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        ApplyGcpAuth(consumerConfig);

        ConsumerBuilder<string?, string> consumerBuilder = new ConsumerBuilder<string?, string>(consumerConfig);

        if (_settings.UseGcpAuth)
        {
            consumerBuilder.SetOAuthBearerTokenRefreshHandler((consumer, _) => RefreshGcpToken(consumer));
        }

        using IConsumer<string?, string> consumer = consumerBuilder.Build();
        consumer.Subscribe(topic);
        _logger.LogInformation("Message bus subscribed. Topic={Topic} GroupId={GroupId}", topic, _settings.ConsumerGroupId);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string?, string> result;

                try
                {
                    result = consumer.Consume(ct);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error. Topic={Topic} Reason={Reason}", topic, ex.Error.Reason);
                    continue;
                }

                try
                {
                    TMessage message = JsonSerializer.Deserialize<TMessage>(result.Message.Value)
                        ?? throw new InvalidOperationException("Kafka message deserialized to null.");

                    await handler(message, ct);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Message handler failed; message will be redelivered. Topic={Topic} Partition={Partition} Offset={Offset}",
                        topic,
                        result.Partition.Value,
                        result.Offset.Value);

                    // Rewind so the uncommitted message is consumed again.
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(RetryDelay, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private void ApplyGcpAuth(ClientConfig config)
    {
        if (_settings.UseGcpAuth)
        {
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
            config.SaslMechanism = SaslMechanism.OAuthBearer;
        }
    }

    private static void RefreshGcpToken(IClient client)
    {
        GoogleCredential credential = GoogleCredential
            .GetApplicationDefault()
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        string token = credential.UnderlyingCredential
            .GetAccessTokenForRequestAsync().GetAwaiter().GetResult();
        client.OAuthBearerSetToken(token, DateTimeOffset.UtcNow.AddMinutes(55).ToUnixTimeMilliseconds(), string.Empty);
    }
}
