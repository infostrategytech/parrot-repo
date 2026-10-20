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

    public async Task<(List<Customer> Items, int Total)> ListAsync(
        string ownerUserId,
        string? search,
        string? stage,
        string? channel,
        int skip,
        int take,
        CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        IQueryable<Customer> query = db.Customers
            .Include(c => c.Identities)
            .Where(c => c.OwnerUserId == ownerUserId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string escaped = search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            string pattern = $"%{escaped}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Name, pattern, @"\") ||
                c.Identities.Any(i =>
                    EF.Functions.ILike(i.Sender, pattern, @"\") ||
                    (i.Username != null && EF.Functions.ILike(i.Username, pattern, @"\"))));
        }

        if (!string.IsNullOrWhiteSpace(stage))
            query = query.Where(c => c.Stage == stage);

        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(c => c.Identities.Any(i => i.Platform == channel));

        int total = await query.CountAsync(ct);
        List<Customer> items = await query
            .OrderByDescending(c => c.LastSeenAt ?? c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerUserId, ct);
    }

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

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

    public async Task<Customer?> UpdateStageAsync(
        string ownerUserId, Guid id, string stage, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        Customer? customer = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerUserId, ct);
        if (customer is null) return null;
        customer.Stage = stage;
        customer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer?> UpdateTagsAsync(
        string ownerUserId, Guid id, List<string> tags, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        Customer? customer = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerUserId, ct);
        if (customer is null) return null;
        customer.Tags = tags;
        customer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    private static bool IsIdentityConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdentityUniqueConstraint,
        };
}
