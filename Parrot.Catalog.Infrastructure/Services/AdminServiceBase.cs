using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Shared machinery for the write side: one transaction per operation, the tenant row
/// provisioned on first use, audit and outbox rows written in the same transaction,
/// and Postgres errors translated into the API's domain exceptions.
/// </summary>
public abstract class AdminServiceBase
{
    protected AdminServiceBase(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, ItemEventWriter itemEvents)
    {
        Db = db;
        ItemEvents = itemEvents;
        Tenant = tenant;
        Clock = clock;
        Metrics = metrics;
    }

    protected CatalogDbContext Db { get; }

    /// <summary>Shared by every service in the request, so nested writes add to one set.</summary>
    protected ItemEventWriter ItemEvents { get; }

    protected ITenantContext Tenant { get; }

    protected TimeProvider Clock { get; }

    protected CatalogMetrics Metrics { get; }

    protected Guid TenantId => Tenant.TenantId;

    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;

    protected async Task<T> WriteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (Db.Database.CurrentTransaction is not null)
        {
            T inner = await work(ct);
            await SaveAsync(ct);
            return inner;
        }

        await using IDbContextTransaction tx = await Db.Database.BeginTransactionAsync(ct);
        ItemEvents.Reset();
        try
        {
            await EnsureTenantAsync(ct);
            T result = await work(ct);
            await SaveAsync(ct);

            // One whole-item event per product touched, read after every row is saved,
            // so it describes the item as committed (hidden flag and photos included).
            await ItemEvents.WriteAsync(ct);
            await SaveAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (PostgresException ex)
        {
            throw PgErrors.Translate(ex, Metrics);
        }
        finally
        {
            ItemEvents.Reset();
        }
    }

    protected Task WriteAsync(Func<CancellationToken, Task> work, CancellationToken ct)
        => WriteAsync<object?>(async c =>
        {
            await work(c);
            return null;
        }, ct);

    /// <summary>
    /// Makes the workspace's tenant row on its first write, and records the account
    /// that owns it whenever the token says, so events can name the owner even when
    /// a later write comes from a token without the claim (a service or a tool).
    /// </summary>
    protected async Task EnsureTenantAsync(CancellationToken ct)
    {
        Tenant? row = await Db.Tenants.FirstOrDefaultAsync(t => t.Id == TenantId, ct);
        if (row is null)
        {
            DateTime now = Now;
            Db.Tenants.Add(new Tenant { Id = TenantId, TenantId = TenantId, Name = $"tenant-{TenantId:N}", OwnerId = Tenant.OwnerId, CreatedAt = now, UpdatedAt = now });
            await SaveAsync(ct);
        }
        else if (Tenant.OwnerId is { } owner && row.OwnerId != owner)
        {
            row.OwnerId = owner;
            row.UpdatedAt = Now;
            await SaveAsync(ct);
        }
    }

    protected void Audit(string action, string entityType, Guid entityId, object? before, object? after)
    {
        Db.AuditLog.Add(new AuditLogEntry
        {
            TenantId = TenantId,
            Actor = Tenant.Actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString("D"),
            Before = before is null ? null : Json.Serialize(before),
            After = after is null ? null : Json.Serialize(after),
            RequestId = Tenant.RequestId,
            OccurredAt = Now,
        });
    }

    protected void Enqueue(string aggregateType, Guid aggregateId, string eventType, object payload)
    {
        string json = Json.Serialize(payload);
        if (ItemEventWriter.ProductOf(aggregateType, aggregateId, json) is { } productId)
        {
            ItemEvents.Touch(productId);
        }

        Db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            OwnerId = Tenant.OwnerId,
            UserId = ItemEventWriter.UserOf(Tenant),
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            SchemaVersion = 1,
            Payload = json,
            CreatedAt = Now,
        });
    }

    protected void Stamp(ITimestamped entity, bool created)
    {
        DateTime now = Now;
        if (created)
        {
            entity.CreatedAt = now;
        }

        entity.UpdatedAt = now;
    }

    protected async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw PgErrors.Translate(pg, Metrics);
        }
    }
}

public static class PgErrors
{
    public static DomainException Translate(PostgresException ex, CatalogMetrics? metrics)
    {
        switch (ex.SqlState)
        {
            case PostgresErrorCodes.UniqueViolation:
                return new ConflictException(DescribeUnique(ex.ConstraintName));
            case PostgresErrorCodes.ExclusionViolation:
                return new ConflictException("An active price for the same offer, currency and price type already covers part of that period.");
            case PostgresErrorCodes.ForeignKeyViolation:
                return new ValidationException("A referenced record does not exist in this tenant.");
            case PostgresErrorCodes.CheckViolation:
                return new ValidationException($"The request violates a catalog rule ({ex.ConstraintName}).");
            case PostgresErrorCodes.InsufficientPrivilege:
                metrics?.RlsError();
                return new ForbiddenException("The database refused the operation for this role or tenant.");
            default:
                throw ex;
        }
    }

    private static string DescribeUnique(string? constraint) => constraint switch
    {
        "ix_products_tenant_id_catalog_id_sku" or "ix_product_variants_tenant_id_catalog_id_sku" => "That SKU already exists in this catalog.",
        "ix_products_tenant_id_catalog_id_slug" or "ix_categories_tenant_id_catalog_id_slug" => "That slug already exists in this catalog.",
        "ix_catalogs_tenant_id_key" => "A catalog with that key already exists.",
        "ix_categories_tenant_id_catalog_id_key" => "A category with that key already exists in this catalog.",
        "ix_product_types_tenant_id_key" => "A product type with that key already exists.",
        "ix_attribute_definitions_tenant_id_product_type_id_type_version_key" => "An attribute with that key already exists on this type version.",
        "ix_product_relationships_tenant_id_source_id_target_id_relationship_type" => "That relationship already exists.",
        _ => "A record with the same unique key already exists.",
    };
}
