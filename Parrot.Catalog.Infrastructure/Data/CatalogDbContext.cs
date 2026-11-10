using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Data;

/// <summary>
/// The writer's view of the schema: base tables, connected as a member of
/// catalog_writer. Migrations are generated from this context.
/// </summary>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<CatalogEntity> Catalogs => Set<CatalogEntity>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<CategoryClosure> CategoryClosure => Set<CategoryClosure>();

    public DbSet<ProductType> ProductTypes => Set<ProductType>();

    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductAttributeIndex> ProductAttributeIndex => Set<ProductAttributeIndex>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<Price> Prices => Set<Price>();

    public DbSet<Availability> Availability => Set<Availability>();

    public DbSet<ProductRelationship> ProductRelationships => Set<ProductRelationship>();

    public DbSet<MediaAsset> Media => Set<MediaAsset>();

    public DbSet<ProductVersion> ProductVersions => Set<ProductVersion>();

    public DbSet<CatalogDocument> CatalogDocuments => Set<CatalogDocument>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ConsumerInboxEntry> ConsumerInbox => Set<ConsumerInboxEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("btree_gist");
        ConfigureEntities(builder);
        ModelConventions.Apply(builder, asViews: false);
    }

    /// <summary>Keys, relationships, indexes and lengths. Shared with the read context so view entities line up.</summary>
    internal static void ConfigureEntities(ModelBuilder builder)
    {
        builder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.OwnerId).HasMaxLength(256);
            e.ToTable(t => t.HasCheckConstraint("ck_tenants_self", "id = tenant_id"));
        });

        builder.Entity<CatalogEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Key).HasMaxLength(64);
            e.Property(c => c.Name).HasMaxLength(256);
            e.Property(c => c.DefaultCurrency).HasMaxLength(3).IsFixedLength();
            e.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Category>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Key).HasMaxLength(64);
            e.Property(c => c.Name).HasMaxLength(256);
            e.Property(c => c.Slug).HasMaxLength(256);
            e.Property(c => c.Description).HasMaxLength(2000);
            e.HasIndex(c => new { c.TenantId, c.CatalogId, c.Key }).IsUnique();
            e.HasIndex(c => new { c.TenantId, c.CatalogId, c.Slug }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(c => new { c.TenantId, c.ParentId });
            e.HasOne<CatalogEntity>().WithMany().HasForeignKey(c => c.CatalogId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CategoryClosure>(e =>
        {
            e.HasKey(c => new { c.TenantId, c.AncestorId, c.DescendantId });
            e.HasIndex(c => new { c.TenantId, c.DescendantId });
            e.HasOne<Category>().WithMany().HasForeignKey(c => c.AncestorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany().HasForeignKey(c => c.DescendantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProductType>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Key).HasMaxLength(64);
            e.Property(t => t.Name).HasMaxLength(256);
            e.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AttributeDefinition>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Key).HasMaxLength(64);
            e.Property(d => d.Label).HasMaxLength(128);
            e.Property(d => d.Unit).HasMaxLength(32);
            e.Property(d => d.DisplayGroup).HasMaxLength(128);
            e.HasIndex(d => new { d.TenantId, d.ProductTypeId, d.TypeVersion, d.Key }).IsUnique();
            e.HasOne<ProductType>().WithMany().HasForeignKey(d => d.ProductTypeId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_attribute_definitions_key", "key ~ '^[a-z][a-z0-9_]{0,63}$'"));
        });

        builder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Sku).HasMaxLength(64);
            e.Property(p => p.Name).HasMaxLength(256);
            e.Property(p => p.Slug).HasMaxLength(256);
            e.Property(p => p.Description).HasMaxLength(20000);
            e.Property(p => p.Duration).HasMaxLength(64);
            e.Property(p => p.LeadTime).HasMaxLength(64);
            e.Property(p => p.FormDraft).HasColumnType("jsonb");
            e.Property(p => p.CreatedBy).HasMaxLength(256);
            e.Property(p => p.UpdatedBy).HasMaxLength(256);
            e.Property<NpgsqlTsVector>("SearchTsv").HasComputedColumnSql("to_tsvector('simple', search_text)", stored: true);
            e.HasIndex(p => new { p.TenantId, p.CatalogId, p.Sku }).IsUnique();
            e.HasIndex(p => new { p.TenantId, p.CatalogId, p.Slug }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(p => new { p.TenantId, p.CatalogId, p.Status, p.PublishedAt });
            e.HasIndex(p => new { p.TenantId, p.CategoryId });
            e.HasIndex(p => new { p.TenantId, p.ProductTypeId, p.TypeVersion });
            e.HasIndex("SearchTsv").HasMethod("GIN");
            e.HasOne<CatalogEntity>().WithMany().HasForeignKey(p => p.CatalogId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductType>().WithMany().HasForeignKey(p => p.ProductTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_products_type_version", "type_version >= 1");
                t.HasCheckConstraint("ck_products_attributes_object", "jsonb_typeof(attributes) = 'object'");
                t.HasCheckConstraint("ck_products_form_draft_object", "form_draft IS NULL OR jsonb_typeof(form_draft) = 'object'");
                t.HasCheckConstraint("ck_products_attributes_size", "pg_column_size(attributes) <= 65536");
            });
        });

        builder.Entity<ProductVariant>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.Sku).HasMaxLength(64);
            e.Property(v => v.Name).HasMaxLength(256);
            e.HasIndex(v => new { v.TenantId, v.CatalogId, v.Sku }).IsUnique();
            e.HasIndex(v => new { v.TenantId, v.ProductId });
            e.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_product_variants_attributes_object", "jsonb_typeof(attributes) = 'object'");
                t.HasCheckConstraint("ck_product_variants_attributes_size", "pg_column_size(attributes) <= 65536");
            });
        });

        builder.Entity<ProductAttributeIndex>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).UseIdentityByDefaultColumn();
            e.Property(i => i.AttributeKey).HasMaxLength(64);
            e.Property(i => i.TextValue).HasMaxLength(1024);
            e.Property(i => i.NumericValue).HasColumnType("numeric(28,8)");
            e.HasIndex(i => new { i.TenantId, i.AttributeKey, i.NumericValue });
            e.HasIndex(i => new { i.TenantId, i.AttributeKey, i.TextValue });
            e.HasIndex(i => new { i.TenantId, i.AttributeKey, i.BoolValue });
            e.HasIndex(i => new { i.TenantId, i.AttributeKey, i.DateValue });
            e.HasIndex(i => new { i.TenantId, i.ProductId });
            e.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Offer>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Name).HasMaxLength(256);
            e.Property(o => o.Description).HasMaxLength(4000);
            e.Property(o => o.Unit).HasMaxLength(32);
            e.HasIndex(o => new { o.TenantId, o.ProductId });
            e.HasIndex(o => new { o.TenantId, o.VariantId });
            e.HasOne<Product>().WithMany().HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(o => o.VariantId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_offers_target", "(product_id IS NULL) <> (variant_id IS NULL)");
                t.HasCheckConstraint("ck_offers_validity", "valid_until IS NULL OR valid_until > valid_from");
                t.HasCheckConstraint("ck_offers_unit", "unit IS NULL OR length(unit) > 0");
            });
        });

        builder.Entity<Price>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            e.HasIndex(p => new { p.TenantId, p.OfferId });
            e.HasOne<Offer>().WithMany().HasForeignKey(p => p.OfferId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_prices_amount", "amount >= 0");
                t.HasCheckConstraint("ck_prices_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_prices_validity", "valid_until IS NULL OR valid_until > valid_from");
            });
        });

        builder.Entity<Availability>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Region).HasMaxLength(8);
            e.HasIndex(a => new { a.TenantId, a.ProductId, a.Region });
            e.HasIndex(a => new { a.TenantId, a.VariantId, a.Region });
            e.HasOne<Product>().WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(a => a.VariantId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_availability_target", "(product_id IS NULL) <> (variant_id IS NULL)");
                t.HasCheckConstraint("ck_availability_validity", "valid_until IS NULL OR valid_until > valid_from");
                t.HasCheckConstraint("ck_availability_quantities", "(stock_qty IS NULL OR stock_qty >= 0) AND (capacity IS NULL OR capacity >= 0)");
                t.HasCheckConstraint("ck_availability_lead_time", "lead_time_days IS NULL OR (lead_time_days >= 0 AND mode = 'made_to_order')");
            });
        });

        builder.Entity<ProductRelationship>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.TenantId, r.SourceId, r.TargetId, r.RelationshipType }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.TargetId });
            e.HasOne<Product>().WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(r => r.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_product_relationships_distinct", "source_id <> target_id"));
        });

        builder.Entity<MediaAsset>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.S3Key).HasMaxLength(1024);
            e.Property(m => m.MimeType).HasMaxLength(128);
            e.Property(m => m.AltText).HasMaxLength(512);
            e.Property(m => m.Title).HasMaxLength(256);
            e.HasIndex(m => new { m.TenantId, m.ProductId });
            e.HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(m => m.VariantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProductVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.ContentHash).HasMaxLength(64);
            e.Property(v => v.PublishedBy).HasMaxLength(256);
            e.HasIndex(v => new { v.TenantId, v.ProductId, v.VersionNumber }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CatalogDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.ContentHash).HasMaxLength(64);
            e.HasIndex(d => new { d.TenantId, d.ProductId, d.VariantId }).IsUnique().AreNullsDistinct(false);
            e.HasOne<Product>().WithMany().HasForeignKey(d => d.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).UseIdentityByDefaultColumn();
            e.Property(a => a.Actor).HasMaxLength(256);
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.EntityType).HasMaxLength(64);
            e.Property(a => a.EntityId).HasMaxLength(64);
            e.Property(a => a.RequestId).HasMaxLength(128);
            e.HasIndex(a => new { a.TenantId, a.OccurredAt });
        });

        builder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.AggregateType).HasMaxLength(64);
            e.Property(o => o.EventType).HasMaxLength(64);
            e.Property(o => o.OwnerId).HasMaxLength(256);
            e.Property(o => o.UserId).HasMaxLength(256);
            e.Property(o => o.LastError).HasMaxLength(2000);
            e.HasIndex(o => o.CreatedAt).HasFilter("published_at IS NULL");
            e.HasIndex(o => new { o.TenantId, o.CreatedAt });
        });

        builder.Entity<ConsumerInboxEntry>(e =>
        {
            e.HasKey(c => new { c.Consumer, c.EventId });
            e.Property(c => c.Consumer).HasMaxLength(64);
        });
    }
}

/// <summary>
/// The reader's view of the schema: the v_* views that expose published data only,
/// connected as a member of catalog_reader. It has no migrations and never writes.
/// </summary>
public class CatalogReadDbContext : DbContext
{
    public CatalogReadDbContext(DbContextOptions<CatalogReadDbContext> options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<CatalogEntity> Catalogs => Set<CatalogEntity>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<CategoryClosure> CategoryClosure => Set<CategoryClosure>();

    public DbSet<ProductType> ProductTypes => Set<ProductType>();

    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductAttributeIndex> ProductAttributeIndex => Set<ProductAttributeIndex>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<Price> Prices => Set<Price>();

    public DbSet<Availability> Availability => Set<Availability>();

    public DbSet<ProductRelationship> ProductRelationships => Set<ProductRelationship>();

    public DbSet<MediaAsset> Media => Set<MediaAsset>();

    public DbSet<CatalogDocument> CatalogDocuments => Set<CatalogDocument>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        foreach (Type t in new[] { typeof(CatalogEntity), typeof(Category), typeof(CategoryClosure), typeof(ProductType), typeof(AttributeDefinition), typeof(Product), typeof(ProductVariant), typeof(ProductAttributeIndex), typeof(Offer), typeof(Price), typeof(Availability), typeof(ProductRelationship), typeof(MediaAsset), typeof(CatalogDocument) })
        {
            builder.Entity(t);
        }

        builder.Entity<CategoryClosure>().HasKey(c => new { c.TenantId, c.AncestorId, c.DescendantId });
        builder.Ignore<Tenant>();
        builder.Ignore<ProductVersion>();
        builder.Ignore<AuditLogEntry>();
        builder.Ignore<OutboxMessage>();
        builder.Ignore<ConsumerInboxEntry>();
        builder.Entity<Product>().Property<NpgsqlTsVector>("SearchTsv");
        ModelConventions.Apply(builder, asViews: true);
    }
}
