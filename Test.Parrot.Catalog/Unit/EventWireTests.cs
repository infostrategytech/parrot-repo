using System.Text.Json;
using Parrot.Catalog.Application.Events;
using Xunit;

namespace Test.Parrot.Catalog.Unit;

/// <summary>
/// The two message shapes on the bus. catalog.events keeps tenant_id as the workspace,
/// which its consumers read; catalog.items names things the way MessagePersisted does:
/// tenant_id is the owner, workspace_id the business, user_id who made the change.
/// </summary>
public class EventWireTests
{
    private static readonly Guid Workspace = Guid.Parse("8f14e45f-ceea-467f-a0e6-2b9d1c7f3a21");
    private static readonly Guid EventId = Guid.Parse("0b6f5f1e-6d2a-4c1e-9a55-3f0c1b2d4e6f");
    private static readonly Guid Product = Guid.Parse("3c9d2b7a-1e4f-4a8b-b6c2-9d0e1f2a3b4c");

    private static EventEnvelope Envelope(string type, string? owner) => new(
        EventId, Workspace, owner, "user-1", AggregateTypes.Product, Product, type, 1,
        new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), JsonDocument.Parse("""{"product_id":"x"}""").RootElement.Clone());

    [Fact]
    public void Row_events_keep_the_workspace_as_tenant_and_leave_the_owner_out()
    {
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(Envelope(CatalogEventTypes.ProductUpdated, "owner-1"), EventEnvelope.WireOptions));
        JsonElement root = wire.RootElement;

        Assert.Equal(Workspace.ToString("D"), root.GetProperty("tenant_id").GetString());
        Assert.Equal(Workspace.ToString("D"), root.GetProperty("workspace_id").GetString());
        Assert.Equal("user-1", root.GetProperty("user_id").GetString());
        Assert.False(root.TryGetProperty("owner_id", out _));

        // The cache invalidator reads it back the same way.
        EventEnvelope read = JsonSerializer.Deserialize<EventEnvelope>(wire.RootElement.GetRawText(), EventEnvelope.WireOptions)!;
        Assert.Equal(Workspace, read.TenantId);
        Assert.Equal(EventId, read.EventId);
    }

    [Fact]
    public void Item_events_send_the_owner_as_tenant_and_the_business_as_workspace()
    {
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(ItemEventMessage.From(Envelope(CatalogEventTypes.ItemChanged, "owner-1")), EventEnvelope.WireOptions));
        JsonElement root = wire.RootElement;

        Assert.Equal("owner-1", root.GetProperty("tenant_id").GetString());
        Assert.Equal(Workspace.ToString("D"), root.GetProperty("workspace_id").GetString());
        Assert.Equal("user-1", root.GetProperty("user_id").GetString());
        Assert.Equal(EventId.ToString("D"), root.GetProperty("event_id").GetString());
        Assert.Equal("ItemChanged", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("owner_id", out _));
    }

    [Fact]
    public void An_item_event_whose_owner_is_unknown_says_so_with_a_null_tenant()
    {
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(ItemEventMessage.From(Envelope(CatalogEventTypes.ItemRemoved, null)), EventEnvelope.WireOptions));

        Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("tenant_id").ValueKind);
        Assert.Equal(Workspace.ToString("D"), wire.RootElement.GetProperty("workspace_id").GetString());
    }
}
