using Confluent.Kafka;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class KafkaSubscriptionOptionsTests
{
    private static readonly KafkaSettings Settings = new() { BootstrapServers = "kafka:9092", ConsumerGroupId = "message-processor" };

    // The worker's behaviour must not change: its group, from the beginning, manual commits.
    [Fact]
    public void Default_UsesTheConfiguredGroupFromTheBeginning()
    {
        ConsumerConfig config = KafkaMessageBus.ConsumerConfigFor(Settings, SubscriptionOptions.Default);

        Assert.Equal("message-processor", config.GroupId);
        Assert.Equal(AutoOffsetReset.Earliest, config.AutoOffsetReset);
        Assert.False(config.EnableAutoCommit);
        Assert.Equal("kafka:9092", config.BootstrapServers);
    }

    // Live delivery reads every partition in a group of its own and does not replay history.
    [Fact]
    public void Overrides_GiveTheSubscriptionItsOwnGroupStartingAtNewMessages()
    {
        ConsumerConfig config = KafkaMessageBus.ConsumerConfigFor(
            Settings, new SubscriptionOptions(GroupId: "realtime-gateway", StartFromLatest: true));

        Assert.Equal("realtime-gateway", config.GroupId);
        Assert.Equal(AutoOffsetReset.Latest, config.AutoOffsetReset);
        Assert.False(config.EnableAutoCommit);
    }
}
