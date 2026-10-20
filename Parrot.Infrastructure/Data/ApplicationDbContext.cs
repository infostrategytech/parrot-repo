using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Parrot.Application.Models;
using Parrot.Domain.Entities;

namespace Parrot.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
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
            entity.Property(a => a.UserId).IsRequired();
            entity.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(a => a.UserId);

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

        builder.Entity<WhatsAppConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.OwnerUserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerUserId)
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
            entity.HasIndex(c => c.OwnerUserId);
            entity.HasIndex(c => c.PhoneNumberId).IsUnique()
                .HasFilter("\"PhoneNumberId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<FacebookConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.OwnerUserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.VerifyTokenEncrypted).IsRequired();
            entity.Property(c => c.PageId).HasMaxLength(64);
            entity.Property(c => c.PageName).HasMaxLength(128);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            entity.HasIndex(c => c.OwnerUserId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.PageId).IsUnique()
                .HasFilter("\"PageId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<TikTokConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.OwnerUserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.AppId).HasMaxLength(64).IsRequired();
            entity.Property(c => c.AppSecretEncrypted).IsRequired();
            entity.Property(c => c.TikTokOpenId).HasMaxLength(128);
            entity.Property(c => c.DisplayName).HasMaxLength(128);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            entity.HasIndex(c => c.OwnerUserId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.TikTokOpenId).IsUnique()
                .HasFilter("\"TikTokOpenId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<InstagramConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.OwnerUserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(c => c.WebhookIdentifier).HasMaxLength(64).IsRequired();
            entity.Property(c => c.VerifyTokenEncrypted).IsRequired();
            entity.Property(c => c.AccountId).HasMaxLength(64);
            entity.Property(c => c.Username).HasMaxLength(64);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(c => c.WebhookIdentifier).IsUnique();
            // One active Instagram connection per user, and one per Instagram account across Parrot.
            entity.HasIndex(c => c.OwnerUserId).IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");
            entity.HasIndex(c => c.AccountId).IsUnique()
                .HasFilter("\"AccountId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        });

        builder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.OwnerUserId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerUserId)
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

            entity.HasIndex(c => c.OwnerUserId);
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
}
