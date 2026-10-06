namespace Parrot.Catalog.Domain.Entities;

/// <summary>Every catalog row carries the tenant; RLS is keyed on it (ADR-004).</summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public interface ISoftDeletable
{
    DateTime? DeletedAt { get; set; }
}

public interface ITimestamped
{
    DateTime CreatedAt { get; set; }

    DateTime UpdatedAt { get; set; }
}
