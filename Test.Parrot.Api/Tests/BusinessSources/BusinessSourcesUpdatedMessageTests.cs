using System.Text.Json;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Messaging;

namespace Test.Parrot.Api.Tests.BusinessSources;

// The business.knowledge.sources wire contract: camelCase, like messages.persisted.
public sealed class BusinessSourcesUpdatedMessageTests
{
    private static BusinessSourcesUpdatedMessage Message() => new()
    {
        EventId = "evt-1",
        WorkspaceId = "ws-a",
        SnapshotVersion = 3,
        OccurredAt = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        RefreshRequestId = "refresh-1",
        Data = new BusinessData { Name = "Adire House" },
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
    };

    [Fact]
    public void Serialize_WritesEveryNameInCamelCase()
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(Message(), MessageJson.Options));
        JsonElement root = document.RootElement;

        string[] expected = ["eventId", "eventType", "schemaVersion", "workspaceId", "snapshotVersion", "occurredAt", "refreshRequestId", "data", "sources"];
        Assert.Equal(expected, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["sourceId", "externalId", "connectionId", "channel", "kind", "displayName", "handle", "url", "providerData", "text", "capturedAt"],
            root.GetProperty("sources")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Adire House", root.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public void FromStoredPayload_WhenQueuedInTheOldSnakeCaseShape_ReadsEveryField()
    {
        const string stored = """
            {"event_id":"evt-1","event_type":"BusinessSourcesUpdated","schema_version":1,"workspace_id":"ws-a",
             "snapshot_version":3,"occurred_at":"2026-11-18T10:00:00.000000+00:00","refresh_request_id":null,
             "data":{"name":"Adire House"},
             "sources":[{"source_id":"whatsapp:15550001234","external_id":"15550001234","connection_id":"c-1",
               "channel":"whatsapp","kind":"profile","display_name":"Adire House","handle":null,"url":null,
               "provider_data":{"waba_id":"waba-1","phone_number_id":"15550001234"},
               "text":"{\"followers_count\":12}","captured_at":null}]}
            """;

        BusinessSourcesUpdatedMessage message = BusinessSourcesUpdatedMessage.FromStoredPayload(stored);

        Assert.Equal("evt-1", message.EventId);
        Assert.Equal("ws-a", message.WorkspaceId);
        Assert.Equal(3, message.SnapshotVersion);
        Assert.Equal("Adire House", message.Data.Name);
        SourceEntry source = Assert.Single(message.Sources);
        Assert.Equal("whatsapp:15550001234", source.SourceId);
        Assert.Equal("Adire House", source.DisplayName);
        Assert.Equal("waba-1", source.ProviderData!.Value.GetProperty("wabaId").GetString());
        Assert.Equal("15550001234", source.ProviderData!.Value.GetProperty("phoneNumberId").GetString());
        // The text is the channel's own data, carried as a string; its contents are left as they were.
        Assert.Equal("{\"followers_count\":12}", source.Text);
    }

    [Fact]
    public void FromStoredPayload_WhenCamelCase_ReadsItBackUnchanged()
    {
        string stored = JsonSerializer.Serialize(Message(), MessageJson.Options);

        BusinessSourcesUpdatedMessage message = BusinessSourcesUpdatedMessage.FromStoredPayload(stored);

        Assert.Equal(stored, JsonSerializer.Serialize(message, MessageJson.Options));
    }
}
