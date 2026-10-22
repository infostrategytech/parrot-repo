using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Parrot.Application.Models;
using Parrot.Domain.Entities;

namespace Parrot.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceGrant> WorkspaceGrants => Set<WorkspaceGrant>();

    public DbSet<TeamInvite> TeamInvites => Set<TeamInvite>();

    public DbSet<OwnerRole> OwnerRoles => Set<OwnerRole>();

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<WhatsAppConnection> WhatsAppConnections => Set<WhatsAppConnection>();
    public DbSet<FacebookConnection> FacebookConnections => Set<FacebookConnection>();
    public DbSet<TikTokConnection> TikTokConnections => Set<TikTokConnection>();
    public DbSet<InstagramConnection> InstagramConnections => Set<InstagramConnection>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerIdentity> CustomerIdentities => Set<CustomerIdentity>();

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.BusinessName).HasMaxLength(256).IsRequired(false);
            entity.Property(u => u.FirstName).HasMaxLength(80);
            entity.Property(u => u.LastName).HasMaxLength(80);
            entity.Property(u => u.LogoUrl).HasMaxLength(2048);
            entity.Property(u => u.PhoneDialCode).HasMaxLength(8);

            // Sign-in compares against both of the shapes a number is written
            // in. The national half alone identifies an account on its own — a
            // local number such as 08012345678 lands here — so it is unique
            // across the whole table, not merely within a country.
            //
            // The other half of the comparison, PhoneDialCode + PhoneNumber, is
            // an expression rather than a column, so it cannot be declared here.
            // Its unique index is created as raw SQL in the migration that adds
            // it; see AddPhoneNumberLoginIndexes.
            entity.HasIndex(u => u.PhoneNumber).IsUnique()
                .HasFilter("\"PhoneNumber\" IS NOT NULL");

            entity.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // The id is text like an Identity user id, which a tenant's first
        // workspace reuses, and like the owner columns that point at it.
        builder.Entity<Workspace>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.OwnerId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(w => w.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(w => w.Name).HasMaxLength(256);
            entity.Property(w => w.LogoUrl).HasMaxLength(2048);
            entity.Property(w => w.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(w => w.OwnerId);
        });

        // An owner's team. A grant names one workspace, or none for all of the
        // owner's workspaces; each person holds at most one of each, which
        // takes two partial unique indexes because a plain one treats every
        // null workspace as different.
        builder.Entity<WorkspaceGrant>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.OwnerId).IsRequired();
            entity.Property(g => g.UserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(g => g.OwnerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Workspace>().WithMany().HasForeignKey(g => g.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            // A built-in role by id, or one of the owner's own: exactly one of the two.
            entity.Property(g => g.SystemRole).HasMaxLength(64);
            entity.HasOne<OwnerRole>().WithMany().HasForeignKey(g => g.CustomRoleId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint("CK_WorkspaceGrants_OneRole",
                "(\"SystemRole\" IS NULL) <> (\"CustomRoleId\" IS NULL)"));
            entity.Property(g => g.Channels).HasConversion(KeyListConverter, KeyListComparer).HasColumnType("text").IsRequired();
            entity.Property(g => g.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(g => new { g.OwnerId, g.UserId, g.WorkspaceId }).IsUnique()
                .HasFilter("\"WorkspaceId\" IS NOT NULL");
            entity.HasIndex(g => new { g.OwnerId, g.UserId }).IsUnique()
                .HasDatabaseName("IX_WorkspaceGrants_OwnerId_UserId_AllWorkspaces")
                .HasFilter("\"WorkspaceId\" IS NULL");
            entity.HasIndex(g => g.UserId);
        });

        // An owner's own roles. Restrict on the grants' side, so a role in use
        // cannot be deleted out from under the people holding it.
        builder.Entity<OwnerRole>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.OwnerId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(r => r.Name).HasMaxLength(64).IsRequired();
            entity.Property(r => r.NormalizedName).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Permissions).HasConversion(KeyListConverter, KeyListComparer).HasColumnType("text").IsRequired();
            entity.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(r => new { r.OwnerId, r.NormalizedName }).IsUnique();
        });

        // The grants an invite will create are kept as one JSON document: they
        // are only ever read whole, and copied into WorkspaceGrants on accepting.
        builder.Entity<TeamInvite>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.OwnerId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.OwnerId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(i => i.Email).HasMaxLength(256).IsRequired();
            entity.Property(i => i.NormalizedEmail).HasMaxLength(256).IsRequired();
            entity.Property(i => i.FullName).HasMaxLength(80).IsRequired();
            entity.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(i => i.InvitedByUserId).IsRequired();
            entity.Property(i => i.AcceptedByUserId);
            entity.Property(i => i.Grants)
                .HasConversion(
                    grants => JsonSerializer.Serialize(grants, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<GrantSpec>>(json, (JsonSerializerOptions?)null) ?? new List<GrantSpec>(),
                    new ValueComparer<List<GrantSpec>>(
                        (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                        grants => JsonSerializer.Serialize(grants, (JsonSerializerOptions?)null).GetHashCode(),
                        grants => JsonSerializer.Deserialize<List<GrantSpec>>(JsonSerializer.Serialize(grants, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!))
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(i => i.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(i => i.TokenHash).IsUnique();
            entity.HasIndex(i => new { i.OwnerId, i.NormalizedEmail });
        });

        builder.Entity<FeatureFlag>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(128);
            entity.Property(f => f.Note).HasMaxLength(512);
            entity.Property(f => f.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        builder.Entity<Agent>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Name).HasMaxLength(256).IsRequired();
            entity.Property(a => a.Description).HasMaxLength(2000).IsRequired();
            entity.Property(a => a.MascotUrl).HasMaxLength(2048).IsRequired();
            entity.Property(a => a.WorkspaceId).IsRequired();
            entity.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(a => a.WorkspaceId);

            // The channel keys as one comma-separated text column: a handful
            // of short words per agent, read whole and matched in memory. The
            // comparer tells the change tracker when the list changed in place.
            entity.Property(a => a.Channels)
                .HasConversion(
                    channels => string.Join(',', channels),
                    column => column.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    new ValueComparer<List<string>>(
                        (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                        channels => channels.Aggregate(0, (hash, channel) => HashCode.Combine(hash, channel.GetHashCode())),
                        channels => channels.ToList()))
                .HasColumnType("text")
                .IsRequired();

            // The settings as a JSON document, so the orchestrator's shape
            // is stored as it is and the column can be queried if ever needed.
            entity.Property(a => a.SettingsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(a => a.Version).HasDefaultValue(1);
        });

        // A connection belongs to a workspace. A tenant's first workspace has
        // the user's id, so rows from before workspaces still point right.
        builder.Entity<WhatsAppConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.WorkspaceId).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.Name).HasMaxLength(128).IsRequired();
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.VerifyTokenEncrypted).IsRequired();
            entity.Property(c => c.WhatsAppBusinessAccountId).HasMaxLength(64);
            entity.Property(c => c.PhoneNumberId).HasMaxLength(64);
            entity.Property(c => c.DisplayPhoneNumber).HasMaxLength(32);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Ignore(c => c.SetupStatus);
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            entity.HasIndex(c => c.WorkspaceId);
            entity.HasIndex(c => c.PhoneNumberId).IsUnique()
                .HasFilter("\"PhoneNumberId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<FacebookConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.WorkspaceId).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.VerifyTokenEncrypted).IsRequired();
            entity.Property(c => c.PageId).HasMaxLength(64);
            entity.Property(c => c.PageName).HasMaxLength(128);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            entity.HasIndex(c => c.WorkspaceId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.PageId).IsUnique()
                .HasFilter("\"PageId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<TikTokConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.WorkspaceId).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.AppId).HasMaxLength(64).IsRequired();
            entity.Property(c => c.AppSecretEncrypted).IsRequired();
            entity.Property(c => c.TikTokOpenId).HasMaxLength(128);
            entity.Property(c => c.DisplayName).HasMaxLength(128);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            entity.HasIndex(c => c.WorkspaceId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.TikTokOpenId).IsUnique()
                .HasFilter("\"TikTokOpenId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<InstagramConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.WorkspaceId).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.VerifyTokenEncrypted).IsRequired();
            entity.Property(c => c.AccountId).HasMaxLength(64);
            entity.Property(c => c.Username).HasMaxLength(64);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            // One active Instagram connection per user, and one per Instagram account across Parrot.
            entity.HasIndex(c => c.WorkspaceId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.AccountId).IsUnique()
                .HasFilter("\"AccountId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.WorkspaceId).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.Name).HasMaxLength(256).IsRequired();
            entity.Property(c => c.Email).HasMaxLength(256);
            entity.Property(c => c.Stage).HasMaxLength(64).IsRequired().HasDefaultValue("Customer");
            entity.Property(c => c.LastSeenChannel).HasMaxLength(32);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Tags stored as a comma-separated text column, same pattern as Agent.Channels.
            ValueComparer<List<string>> listComparer = new(
                (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                tags => tags.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode())),
                tags => tags.ToList());
            entity.Property(c => c.Tags)
                .HasConversion(
                    tags => string.Join(',', tags),
                    col => col.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    listComparer)
                .HasColumnType("text")
                .IsRequired()
                .HasDefaultValueSql("''");

            entity.HasIndex(c => c.WorkspaceId);
            entity.HasMany(c => c.Identities).WithOne(i => i.Customer).HasForeignKey(i => i.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CustomerIdentity>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Platform).HasMaxLength(32).IsRequired();
            entity.Property(i => i.Sender).HasMaxLength(128).IsRequired();
            entity.Property(i => i.Username).HasMaxLength(128);
            entity.Property(i => i.LinkedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // The primary deduplication guard: one row per (connection, sender) pair.
            entity.HasIndex(i => new { i.ConnectionId, i.Sender }).IsUnique();
            entity.HasIndex(i => i.CustomerId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateConnectionTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        UpdateConnectionTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void UpdateConnectionTimestamps()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<WhatsAppConnection> entry in ChangeTracker.Entries<WhatsAppConnection>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    // Short keys kept as one comma-separated text column, like Agent.Channels.
    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>, string> KeyListConverter = new(
        keys => string.Join(',', keys),
        column => column.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());

    private static readonly ValueComparer<List<string>> KeyListComparer = new(
        (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
        keys => keys.Aggregate(0, (hash, key) => HashCode.Combine(hash, key.GetHashCode())),
        keys => keys.ToList());
}
