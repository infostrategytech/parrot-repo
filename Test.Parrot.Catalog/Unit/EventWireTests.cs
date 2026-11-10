using System.Text.Json;
using Parrot.Catalog.Application.Events;
using Xunit;

namespace Test.Parrot.Catalog.Unit;

/// <summary>
/// The one message shape on both catalog topics, named the way MessagePersisted names
/// things: tenant_id is the owner, workspace_id the business, user_id who made the change.
/// </summary>
public class EventWireTests
{
    private static readonly Guid Workspace = Guid.Parse("8f14e45f-ceea-467f-a0e6-2b9d1c7f3a21");
    private static readonly Guid EventId = Guid.Parse("0b6f5f1e-6d2a-4c1e-9a55-3f0c1b2d4e6f");
    private static readonly Guid Product = Guid.Parse("3c9d2b7a-1e4f-4a8b-b6c2-9d0e1f2a3b4c");

    private static EventEnvelope Envelope(string type, string? owner) => new(
        EventId, Workspace, owner, "user-1", AggregateTypes.Product, Product, type, 1,
        new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), JsonDocument.Parse("""{"product_id":"x"}""").RootElement.Clone());

    private static JsonElement Wire(EventEnvelope envelope)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(CatalogEventMessage.From(envelope), EventEnvelope.WireOptions));
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData(CatalogEventTypes.ProductUpdated)]
    [InlineData(CatalogEventTypes.PriceChanged)]
    [InlineData(CatalogEventTypes.ItemChanged)]
    [InlineData(CatalogEventTypes.ItemRemoved)]
    public void Every_event_sends_the_owner_as_tenant_and_the_business_as_workspace(string type)
    {
        JsonElement root = Wire(Envelope(type, "owner-1"));

        Assert.Equal("owner-1", root.GetProperty("tenant_id").GetString());
        Assert.Equal(Workspace.ToString("D"), root.GetProperty("workspace_id").GetString());
        Assert.Equal("user-1", root.GetProperty("user_id").GetString());
        Assert.Equal(EventId.ToString("D"), root.GetProperty("event_id").GetString());
        Assert.Equal(type, root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("owner_id", out _));
    }

    [Fact]
    public void An_event_whose_owner_is_unknown_says_so_with_a_null_tenant()
    {
        JsonElement root = Wire(Envelope(CatalogEventTypes.ItemRemoved, null));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("tenant_id").ValueKind);
        Assert.Equal(Workspace.ToString("D"), root.GetProperty("workspace_id").GetString());
    }

    [Fact]
    public void A_consumer_reads_the_workspace_from_workspace_id_not_tenant_id()
    {
        string json = JsonSerializer.Serialize(CatalogEventMessage.From(Envelope(CatalogEventTypes.PriceChanged, "owner-1")), EventEnvelope.WireOptions);

        EventEnvelope read = JsonSerializer.Deserialize<CatalogEventMessage>(json, EventEnvelope.WireOptions)!.ToEnvelope()!;

        Assert.Equal(Workspace, read.WorkspaceId);
        Assert.Equal("owner-1", read.OwnerId);
        Assert.Equal("user-1", read.UserId);
    }

    // Messages already on catalog.events from before this change carry the workspace
    // in tenant_id and have no workspace_id; the cache invalidator must still read them.
    [Fact]
    public void A_message_from_before_workspace_id_is_read_by_its_tenant_id()
    {
        string legacy = $$$"""
            {"event_id":"{{{EventId}}}","tenant_id":"{{{Workspace}}}","aggregate_type":"price","aggregate_id":"{{{Product}}}",
             "type":"PriceChanged","schema_version":1,"occurred_at":"2026-11-01T09:00:00Z","payload":{}}
            """;

        EventEnvelope read = JsonSerializer.Deserialize<CatalogEventMessage>(legacy, EventEnvelope.WireOptions)!.ToEnvelope()!;

        Assert.Equal(Workspace, read.WorkspaceId);
        Assert.Null(read.OwnerId);
    }

    [Fact]
    public void A_message_naming_no_workspace_is_dropped()
    {
        string json = $$$"""{"event_id":"{{{EventId}}}","tenant_id":"not-a-guid","aggregate_type":"price","aggregate_id":"{{{Product}}}","type":"PriceChanged","schema_version":1,"occurred_at":"2026-11-01T09:00:00Z","payload":{}}""";

        Assert.Null(JsonSerializer.Deserialize<CatalogEventMessage>(json, EventEnvelope.WireOptions)!.ToEnvelope());
    }
}
