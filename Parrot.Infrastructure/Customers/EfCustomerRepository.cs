using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Customers;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Customers;

public sealed class EfCustomerRepository(IDbContextFactory<ApplicationDbContext> factory) : ICustomerRepository
{
    // The constraint name EF generates for the unique index on (ConnectionId, Sender).
    private const string IdentityUniqueConstraint = "IX_CustomerIdentities_ConnectionId_Sender";
    // The constraint name EF generates for the unique index on LinkedCustomerId.
    private const string LinkUniqueConstraint = "IX_CustomerLinks_LinkedCustomerId";

    public async Task<(List<Customer> Items, int Total)> ListAsync(
        string ownerUserId,
        string? search,
        string? stage,
        string? channel,
        int skip,
        int take,
        IReadOnlySet<Guid>? connections,
        CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        // Customers that appear as the linked (non-primary) side of a link are hidden from the list.
        IQueryable<Guid> linkedIds = db.CustomerLinks
            .Where(l => l.WorkspaceId == ownerUserId)
            .Select(l => l.LinkedCustomerId);

        IQueryable<Customer> query = db.Customers
            .Include(c => c.Identities)
            .Where(c => c.WorkspaceId == ownerUserId && !linkedIds.Contains(c.Id));

        // A team member limited to some channels sees the customers who wrote on them.
        if (connections is not null)
        {
            List<Guid> reachable = [.. connections];
            query = query.Where(c => c.Identities.Any(i => reachable.Contains(i.ConnectionId)));
        }

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

        await MergeLinkedIdentitiesAsync(db, items, ct);
        return (items, total);
    }

    public async Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        Customer? customer = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == ownerUserId, ct);
        if (customer is not null)
            await MergeLinkedIdentitiesAsync(db, [customer], ct);
        return customer;
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
            WorkspaceId = ownerUserId,
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
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == ownerUserId, ct);
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
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == ownerUserId, ct);
        if (customer is null) return null;
        customer.Tags = tags;
        customer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer?> UpdateChannelIdentifierAsync(
        string ownerUserId, Guid id, string? channelPlatform, string? channelIdentifier, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        Customer? customer = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == ownerUserId, ct);
        if (customer is null) return null;
        customer.ChannelPlatform = channelPlatform;
        customer.ChannelIdentifier = channelIdentifier;
        customer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    // ── Profile links ────────────────────────────────────────────────────

    public async Task<(Customer Primary, List<CustomerLink> Links)?> GetLinksAsync(
        string ownerUserId, Guid id, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        // Load as primary first (the common path).
        Customer? primary = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == ownerUserId, ct);

        if (primary is null)
        {
            // Maybe it's a linked (non-primary) customer; resolve to its primary.
            CustomerLink? asLinked = await db.CustomerLinks
                .Where(l => l.LinkedCustomerId == id && l.WorkspaceId == ownerUserId)
                .FirstOrDefaultAsync(ct);
            if (asLinked is null) return null;
            primary = await db.Customers
                .Include(c => c.Identities)
                .FirstOrDefaultAsync(c => c.Id == asLinked.PrimaryCustomerId && c.WorkspaceId == ownerUserId, ct);
            if (primary is null) return null;
        }

        List<CustomerLink> links = await LoadLinksWithIdentitiesAsync(db, primary.Id, ct);
        MergeLinkedIdentities(primary, links);
        return (primary, links);
    }

    public async Task<(Customer Primary, List<CustomerLink> Links)?> FindByIdentityAsync(
        string ownerUserId, Guid connectionId, string sender, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        CustomerIdentity? identity = await db.CustomerIdentities
            .FirstOrDefaultAsync(i => i.ConnectionId == connectionId && i.Sender == sender, ct);
        if (identity is null) return null;

        Guid customerId = identity.CustomerId;

        // If this customer is itself a linked (non-primary) profile, resolve to the primary.
        CustomerLink? asLinked = await db.CustomerLinks
            .Where(l => l.LinkedCustomerId == customerId && l.WorkspaceId == ownerUserId)
            .FirstOrDefaultAsync(ct);
        if (asLinked is not null) customerId = asLinked.PrimaryCustomerId;

        Customer? primary = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == customerId && c.WorkspaceId == ownerUserId, ct);
        if (primary is null) return null;

        List<CustomerLink> links = await LoadLinksWithIdentitiesAsync(db, primary.Id, ct);
        MergeLinkedIdentities(primary, links);
        return (primary, links);
    }

    public async Task<(Customer Primary, List<CustomerLink> Links)> LinkAsync(
        string ownerUserId, Guid primaryId, Guid linkedId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        Customer? primary = await db.Customers
            .FirstOrDefaultAsync(c => c.Id == primaryId && c.WorkspaceId == ownerUserId, ct);
        if (primary is null) throw new NotFoundException("Customer not found.");

        Customer? linked = await db.Customers
            .FirstOrDefaultAsync(c => c.Id == linkedId && c.WorkspaceId == ownerUserId, ct);
        if (linked is null) throw new NotFoundException("Customer not found.");

        // The primary itself must not already be a linked (non-primary) profile.
        bool primaryIsLinked = await db.CustomerLinks
            .AnyAsync(l => l.LinkedCustomerId == primaryId, ct);
        if (primaryIsLinked) throw new ConflictException("Customer is already a linked profile.");

        // The profile being linked must not itself have linked profiles.
        bool linkedHasLinks = await db.CustomerLinks
            .AnyAsync(l => l.PrimaryCustomerId == linkedId, ct);
        if (linkedHasLinks) throw new ConflictException("Customer already has linked profiles.");

        CustomerLink link = new()
        {
            WorkspaceId = ownerUserId,
            PrimaryCustomerId = primaryId,
            LinkedCustomerId = linkedId,
        };
        db.CustomerLinks.Add(link);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLinkConflict(ex))
        {
            throw new ConflictException("Customer is already linked to another profile.");
        }

        // Reload with full identities for the response.
        Customer reloaded = (await db.Customers.Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == primaryId, ct))!;
        List<CustomerLink> links = await LoadLinksWithIdentitiesAsync(db, primaryId, ct);
        MergeLinkedIdentities(reloaded, links);
        return (reloaded, links);
    }

    public async Task<(Customer Primary, List<CustomerLink> Links)> UnlinkAsync(
        string ownerUserId, Guid primaryId, Guid linkId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        Customer? primary = await db.Customers
            .Include(c => c.Identities)
            .FirstOrDefaultAsync(c => c.Id == primaryId && c.WorkspaceId == ownerUserId, ct);
        if (primary is null) throw new NotFoundException("Customer not found.");

        CustomerLink? link = await db.CustomerLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.PrimaryCustomerId == primaryId && l.WorkspaceId == ownerUserId, ct);
        if (link is null) throw new NotFoundException("Customer not found.");

        db.CustomerLinks.Remove(link);
        await db.SaveChangesAsync(ct);

        List<CustomerLink> remaining = await LoadLinksWithIdentitiesAsync(db, primaryId, ct);
        MergeLinkedIdentities(primary, remaining);
        return (primary, remaining);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<List<CustomerLink>> LoadLinksWithIdentitiesAsync(
        ApplicationDbContext db, Guid primaryId, CancellationToken ct) =>
        await db.CustomerLinks
            .Where(l => l.PrimaryCustomerId == primaryId)
            .Include(l => l.LinkedCustomer).ThenInclude(lc => lc.Identities)
            .ToListAsync(ct);

    // Appends linked customers' identities to the primary's Identities list in memory.
    // Safe to call after the DbContext is done querying: no change tracking side-effects.
    private static void MergeLinkedIdentities(Customer primary, List<CustomerLink> links)
    {
        foreach (CustomerLink link in links)
            primary.Identities.AddRange(link.LinkedCustomer.Identities);
    }

    private static async Task MergeLinkedIdentitiesAsync(
        ApplicationDbContext db, List<Customer> primaries, CancellationToken ct)
    {
        if (primaries.Count == 0) return;
        List<Guid> ids = [.. primaries.Select(c => c.Id)];
        List<CustomerLink> links = await db.CustomerLinks
            .Where(l => ids.Contains(l.PrimaryCustomerId))
            .Include(l => l.LinkedCustomer).ThenInclude(lc => lc.Identities)
            .ToListAsync(ct);
        foreach (Customer primary in primaries)
            primary.Identities.AddRange(links
                .Where(l => l.PrimaryCustomerId == primary.Id)
                .SelectMany(l => l.LinkedCustomer.Identities));
    }

    private static bool IsIdentityConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdentityUniqueConstraint,
        };

    private static bool IsLinkConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: LinkUniqueConstraint,
        };
}
