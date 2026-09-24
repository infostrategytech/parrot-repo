using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Parrot.Application.Models;
using Parrot.Domain.Entities;

namespace Parrot.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<WhatsAppConnection> WhatsAppConnections => Set<WhatsAppConnection>();
    public DbSet<FacebookConnection> FacebookConnections => Set<FacebookConnection>();

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.BusinessName).HasMaxLength(256).IsRequired();
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
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
