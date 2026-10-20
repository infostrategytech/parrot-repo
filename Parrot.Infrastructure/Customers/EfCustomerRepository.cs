using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Customers;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Customers;

public sealed class EfCustomerRepository(IDbContextFactory<ApplicationDbContext> factory) : ICustomerRepository
{
    // The constraint name EF generates for the unique index on (ConnectionId, Sender).
    private const string IdentityUniqueConstraint = "IX_CustomerIdentities_ConnectionId_Sender";

    public async Task UpsertAsync(
        string ownerUserId,
        Guid connectionId,
        string platform,
        string sender,
        string name,
        string? username,
        DateTime seenAt,
        CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        CustomerIdentity? identity = await db.CustomerIdentities
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.ConnectionId == connectionId && i.Sender == sender, ct);

        if (identity is not null)
        {
            Update(identity.Customer, identity, name, username, platform, seenAt);
            await db.SaveChangesAsync(ct);
            return;
        }

        // New sender: create customer + identity in one transaction.
        Customer customer = new()
        {
            OwnerUserId = ownerUserId,
            Name = name,
            Stage = "Customer",
            LastSeenAt = seenAt,
            LastSeenChannel = platform,
        };
        CustomerIdentity newIdentity = new()
        {
            Customer = customer,
            ConnectionId = connectionId,
            Platform = platform,
            Sender = sender,
            Username = username,
        };
        db.Customers.Add(customer);
        db.CustomerIdentities.Add(newIdentity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsIdentityConflict(ex))
        {
            // A concurrent delivery (Kafka replay or parallel webhook) won the race.
            // Re-query and update instead of inserting.
            await using ApplicationDbContext db2 = await factory.CreateDbContextAsync(ct);
            CustomerIdentity? existing = await db2.CustomerIdentities
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.ConnectionId == connectionId && i.Sender == sender, ct);
            if (existing is null) return;
            Update(existing.Customer, existing, name, username, platform, seenAt);
            await db2.SaveChangesAsync(ct);
        }
    }

    private static void Update(
        Customer customer, CustomerIdentity identity,
        string name, string? username, string platform, DateTime seenAt)
    {
        bool changed = false;

        // A channel-supplied name replaces the sender-id fallback but never overwrites
        // a name that was already enriched (i.e. differs from the raw sender string).
        if (name != identity.Sender && name != customer.Name)
        {
            customer.Name = name;
            changed = true;
        }

        if (username is { Length: > 0 } && username != identity.Username)
        {
            identity.Username = username;
            changed = true;
        }

        if (customer.LastSeenAt is null || seenAt > customer.LastSeenAt)
        {
            customer.LastSeenAt = seenAt;
            customer.LastSeenChannel = platform;
            changed = true;
        }

        if (changed) customer.UpdatedAt = DateTime.UtcNow;
    }

    private static bool IsIdentityConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdentityUniqueConstraint,
        };
}
