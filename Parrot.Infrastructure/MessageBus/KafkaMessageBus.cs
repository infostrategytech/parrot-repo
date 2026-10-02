using System.Text.Json;
using Confluent.Kafka;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

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
                Value = JsonSerializer.Serialize(message, MessageJson.Options),
            },
            cancellationToken);
    }

    public Task SubscribeAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, SubscriptionOptions options, CancellationToken cancellationToken)
        // Consume() blocks the calling thread, so the loop gets a thread of its own.
        => Task.Run(() => ConsumeLoopAsync(topic, handler, options, cancellationToken), CancellationToken.None);

    /// <summary>The consumer settings for one subscription: the bus defaults, with the subscription's overrides.</summary>
    public static ConsumerConfig ConsumerConfigFor(KafkaSettings settings, SubscriptionOptions options) => new ConsumerConfig
    {
        BootstrapServers = settings.BootstrapServers,
        GroupId = options.GroupId ?? settings.ConsumerGroupId,
        AutoOffsetReset = options.StartFromLatest ? AutoOffsetReset.Latest : AutoOffsetReset.Earliest,
        EnableAutoCommit = false,
    };

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }

    private async Task ConsumeLoopAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, SubscriptionOptions options, CancellationToken ct)
    {
        ConsumerConfig consumerConfig = ConsumerConfigFor(_settings, options);
        ApplyGcpAuth(consumerConfig);

        ConsumerBuilder<string?, string> consumerBuilder = new ConsumerBuilder<string?, string>(consumerConfig);

        if (_settings.UseGcpAuth)
        {
            consumerBuilder.SetOAuthBearerTokenRefreshHandler((consumer, _) => RefreshGcpToken(consumer));
        }

        using IConsumer<string?, string> consumer = consumerBuilder.Build();
        consumer.Subscribe(topic);
        _logger.LogInformation("Message bus subscribed. Topic={Topic} GroupId={GroupId}", topic, consumerConfig.GroupId);

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
                    TMessage message = JsonSerializer.Deserialize<TMessage>(result.Message.Value, MessageJson.Options)
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

    public Task SubscribeKeyedAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, KeyedSubscriptionOptions options, CancellationToken cancellationToken)
        // Consume() blocks the calling thread, so the loop gets a thread of its own.
        => Task.Run(() => KeyedLoopAsync(topic, handler, options, cancellationToken), CancellationToken.None);

    private async Task KeyedLoopAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, KeyedSubscriptionOptions options, CancellationToken ct)
    {
        ConsumerConfig consumerConfig = ConsumerConfigFor(_settings, new SubscriptionOptions(options.GroupId));
        // A rebalance moves only the partitions that change owner; the rest keep flowing.
        consumerConfig.PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky;
        ApplyGcpAuth(consumerConfig);

        ConsumerBuilder<string?, string> consumerBuilder = new ConsumerBuilder<string?, string>(consumerConfig);
        if (_settings.UseGcpAuth)
        {
            consumerBuilder.SetOAuthBearerTokenRefreshHandler((consumer, _) => RefreshGcpToken(consumer));
        }

        using IConsumer<string?, string> consumer = consumerBuilder.Build();
        consumer.Subscribe(topic);
        _logger.LogInformation("Message bus subscribed. Topic={Topic} GroupId={GroupId} Mode=keyed", topic, consumerConfig.GroupId);

        KeyedBatchProcessor processor = new KeyedBatchProcessor(options.MaxParallelism);
        MessageFailurePolicy policy = new MessageFailurePolicy();
        TimeSpan backoff = TimeSpan.FromSeconds(1);

        Task Handle(BatchItem<ConsumeResult<string?, string>> item, CancellationToken token) =>
            HandleOneAsync(item.Message, handler, token);

        Task DeadLetter(BatchItem<ConsumeResult<string?, string>> item, Parrot.Application.Messaging.UnprocessableMessageException ex, CancellationToken token) =>
            DeadLetterAsync(item.Message, ex.Reason, options.DeadLetterTopic, token);

        void OnRetry(BatchItem<ConsumeResult<string?, string>> item, int attempt, Exception ex) =>
            _logger.LogWarning(ex, "message.retrying Topic={Topic} Partition={Partition} Offset={Offset} Attempt={Attempt}",
                topic, item.Partition, item.Offset, attempt);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<BatchItem<ConsumeResult<string?, string>>> batch = CollectBatch(consumer, topic, options, ct);
                if (batch.Count == 0) continue;

                // Shutdown stops new batches at once; the one in hand gets the grace period.
                using CancellationTokenSource processing = new CancellationTokenSource();
                using CancellationTokenRegistration onShutdown = ct.Register(() => processing.CancelAfter(options.ShutdownGrace));

                try
                {
                    await processor.ProcessAsync(batch, (item, token) => policy.HandleAsync(item, Handle, DeadLetter, OnRetry, token), processing.Token);
                    Commit(consumer, topic, batch);
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "batch.rewound Topic={Topic} Messages={Messages} BackoffMs={BackoffMs}",
                        topic, batch.Count, backoff.TotalMilliseconds);
                    Rewind(consumer, topic, batch);
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Anything not committed is read again by the next consumer.
        }
        finally
        {
            _logger.LogInformation("worker.stopping Topic={Topic}", topic);
            consumer.Close();
        }
    }

    private List<BatchItem<ConsumeResult<string?, string>>> CollectBatch(
        IConsumer<string?, string> consumer, string topic, KeyedSubscriptionOptions options, CancellationToken ct)
    {
        List<BatchItem<ConsumeResult<string?, string>>> batch = new List<BatchItem<ConsumeResult<string?, string>>>();
        try
        {
            ConsumeResult<string?, string> first = consumer.Consume(ct);
            batch.Add(ToItem(first));

            DateTime deadline = DateTime.UtcNow + options.MaxBatchWait;
            while (batch.Count < options.MaxBatchSize)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                ConsumeResult<string?, string>? next = consumer.Consume(remaining);
                if (next is null) break;
                batch.Add(ToItem(next));
            }
        }
        catch (ConsumeException ex)
        {
            _logger.LogError(ex, "Kafka consume error. Topic={Topic} Reason={Reason}", topic, ex.Error.Reason);
        }

        return batch;

        static BatchItem<ConsumeResult<string?, string>> ToItem(ConsumeResult<string?, string> result) =>
            new(result.Message.Key, result.Partition.Value, result.Offset.Value, result);
    }

    private static async Task HandleOneAsync<TMessage>(
        ConsumeResult<string?, string> result, Func<TMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        // Continue the webhook's trace when it sent one.
        using System.Diagnostics.Activity activity = new System.Diagnostics.Activity("message.process");
        if (result.Message.Headers?.TryGetLastBytes("traceparent", out byte[] traceparent) == true)
        {
            activity.SetParentId(System.Text.Encoding.ASCII.GetString(traceparent));
        }

        activity.Start();

        TMessage message;
        try
        {
            message = JsonSerializer.Deserialize<TMessage>(result.Message.Value, MessageJson.Options)
                ?? throw new Parrot.Application.Messaging.UnprocessableMessageException("payload_null");
        }
        catch (JsonException ex)
        {
            throw new Parrot.Application.Messaging.UnprocessableMessageException("payload_not_deserializable", ex);
        }

        await handler(message, ct);
    }

    private async Task DeadLetterAsync(ConsumeResult<string?, string> result, string reason, string deadLetterTopic, CancellationToken ct)
    {
        Headers headers = new Headers();
        foreach (IHeader header in result.Message.Headers ?? new Headers())
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        headers.Add("dlq.reason", System.Text.Encoding.UTF8.GetBytes(reason));
        headers.Add("dlq.source.topic", System.Text.Encoding.UTF8.GetBytes(result.Topic));
        headers.Add("dlq.source.partition", System.Text.Encoding.UTF8.GetBytes(result.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add("dlq.source.offset", System.Text.Encoding.UTF8.GetBytes(result.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add("dlq.failed_at", System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")));

        // Same key and value, so a fixed worker can replay it as it was.
        await _producer.ProduceAsync(deadLetterTopic, new Message<string?, string>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = headers,
        }, ct);

        _logger.LogError("message.dead_lettered Topic={Topic} Partition={Partition} Offset={Offset} Reason={Reason}",
            result.Topic, result.Partition.Value, result.Offset.Value, reason);
    }

    private void Commit<T>(IConsumer<string?, string> consumer, string topic, IReadOnlyList<BatchItem<T>> batch)
    {
        IReadOnlyDictionary<int, long> offsets = KeyedBatchProcessor.CommitOffsets(batch);
        try
        {
            consumer.Commit(offsets.Select(o => new TopicPartitionOffset(topic, o.Key, o.Value)));
            _logger.LogDebug("batch.committed Topic={Topic} Messages={Messages} Partitions={Partitions}", topic, batch.Count, offsets.Count);
        }
        catch (KafkaException ex)
        {
            // Usually a partition moved to another consumer mid-batch; it re-reads
            // from the last commit, which the handlers' duplicate checks make safe.
            _logger.LogWarning(ex, "batch.commit_failed Topic={Topic} Reason={Reason}", topic, ex.Error.Reason);
        }
    }

    private void Rewind<T>(IConsumer<string?, string> consumer, string topic, IReadOnlyList<BatchItem<T>> batch)
    {
        foreach ((int partition, long offset) in KeyedBatchProcessor.RewindOffsets(batch))
        {
            try
            {
                consumer.Seek(new TopicPartitionOffset(topic, partition, offset));
            }
            catch (KafkaException ex)
            {
                // No longer ours after a rebalance; the new owner reads from the last commit.
                _logger.LogWarning(ex, "batch.rewind_skipped Topic={Topic} Partition={Partition}", topic, partition);
            }
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
