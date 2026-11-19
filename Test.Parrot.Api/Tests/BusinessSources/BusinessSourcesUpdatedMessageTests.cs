using System.Text.Json;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.BusinessSources;

// The business.knowledge.sources wire contract (Omni's version 1): camelCase, the snapshot nested under data.
public sealed class BusinessSourcesUpdatedMessageTests
{
    private static BusinessSourcesUpdatedMessage Message() => new()
    {
        EventId = "evt-1",
        OccurredAt = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new BusinessSourcesSnapshot
        {
            WorkspaceId = "ws-a",
            TenantId = "owner-1",
            Trigger = SnapshotTrigger.ManualRefresh,
            SnapshotVersion = 3,
            Business = new BusinessData { Name = "Adire House" },
            Sources =
            [
                new SourceEntry
                {
                    SourceId = "whatsapp:15550001234",
                    ExternalId = "15550001234",
                    ConnectionId = "8f0c2e8a-9b1d-4c7e-8f6a-3b2c1d0e9f87",
                    Channel = "whatsapp",
                    Kind = "profile",
                    DisplayName = "Adire House",
                    Handle = "adirehouse",
                    Url = "https://adirehouse.ng",
                    Text = "{\"about\":\"Hand-dyed adire\"}",
                    CapturedAt = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
                    ProviderData = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["wabaId"] = "waba-1" }),
                },
            ],
        },
    };

    [Fact]
    public void Serialize_NestsTheSnapshotUnderDataInCamelCase()
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(Message(), MessageJson.Options));
        JsonElement root = document.RootElement;
        JsonElement data = root.GetProperty("data");

        Assert.Equal(["eventId", "schemaVersion", "occurredAt", "correlationId", "data"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["workspaceId", "tenantId", "trigger", "snapshotVersion", "business", "sources"], data.EnumerateObject().Select(p => p.Name));
        Assert.Equal("owner-1", data.GetProperty("tenantId").GetString());
        Assert.Equal("ManualRefresh", data.GetProperty("trigger").GetString());
        Assert.Equal("Adire House", data.GetProperty("business").GetProperty("name").GetString());
        Assert.Equal(
            ["sourceId", "externalId", "connectionId", "channel", "kind", "displayName", "handle", "url", "providerData", "text", "capturedAt"],
            data.GetProperty("sources")[0].EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(SnapshotTrigger.ChannelConnected, "ChannelConnected")]
    [InlineData(SnapshotTrigger.ChannelDisconnected, "ChannelDisconnected")]
    [InlineData(SnapshotTrigger.ManualRefresh, "ManualRefresh")]
    [InlineData(SnapshotTrigger.Scheduled, "Scheduled")]
    [InlineData(SnapshotTrigger.WorkspaceCreated, "WorkspaceCreated")]
    [InlineData(SnapshotTrigger.CatalogSynced, "CatalogSynced")]
    public void Serialize_WritesEachTriggerAsTheAgreedName(SnapshotTrigger trigger, string expected)
    {
        BusinessSourcesUpdatedMessage message = Message() with { Data = Message().Data with { Trigger = trigger } };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(message, MessageJson.Options));

        Assert.Equal(expected, document.RootElement.GetProperty("data").GetProperty("trigger").GetString());
    }

    [Fact]
    public void FromStoredPayload_WhenTheCurrentShape_ReadsItBackUnchanged()
    {
        string stored = JsonSerializer.Serialize(Message(), MessageJson.Options);

        BusinessSourcesUpdatedMessage? message = BusinessSourcesUpdatedMessage.FromStoredPayload(stored);

        Assert.NotNull(message);
        Assert.Equal(stored, JsonSerializer.Serialize(message, MessageJson.Options));
    }

    [Theory]
    [InlineData("""{"event_id":"evt-1","schema_version":1,"workspace_id":"ws-a","snapshot_version":3,"data":{"name":"Adire House"},"sources":[]}""")]
    [InlineData("""{"eventId":"evt-1","schemaVersion":1,"workspaceId":"ws-a","snapshotVersion":3,"data":{"name":"Adire House"},"sources":[]}""")]
    [InlineData("""{"eventId":"evt-1","schemaVersion":2,"occurredAt":"2026-11-18T10:00:00.000000+00:00","correlationId":"c","data":{"workspaceId":"ws-a","tenantId":"o","trigger":"Scheduled","snapshotVersion":1,"business":{"name":"A"},"sources":[]}}""")]
    public void FromStoredPayload_WhenQueuedInAnOlderShape_ReturnsNull(string stored)
    {
        Assert.Null(BusinessSourcesUpdatedMessage.FromStoredPayload(stored));
    }
}
