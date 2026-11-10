using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Domain.Entities;

public sealed class Tenant : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    // Equal to Id; kept so the RLS policy is identical on every table.
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    // The account that owns this workspace (Workspace.OwnerId in Parrot), taken
    // from the owner_id claim of the tokens that write to it. Null until one has.
    public string? OwnerId { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class CatalogEntity : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DefaultCurrency { get; set; } = "USD";

    public CatalogStatus Status { get; set; } = CatalogStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}
