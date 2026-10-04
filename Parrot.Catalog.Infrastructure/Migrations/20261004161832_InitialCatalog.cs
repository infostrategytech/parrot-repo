using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consumer_inbox",
                schema: "catalog",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consumer_inbox", x => new { x.consumer, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_self", "id = tenant_id");
                    table.CheckConstraint("ck_tenants_status", "status IN ('active', 'suspended', 'offboarding')");
                });

            migrationBuilder.CreateTable(
                name: "catalogs",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    default_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalogs", x => x.id);
                    table.CheckConstraint("ck_catalogs_status", "status IN ('active', 'inactive')");
                    table.ForeignKey(
                        name: "fk_catalogs_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "catalog",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_types",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_types", x => x.id);
                    table.CheckConstraint("ck_product_types_kind", "kind IN ('good', 'service', 'subscription', 'bundle')");
                    table.CheckConstraint("ck_product_types_status", "status IN ('active', 'deprecated')");
                    table.ForeignKey(
                        name: "fk_product_types_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "catalog",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_categories_catalog_id",
                        column: x => x.catalog_id,
                        principalSchema: "catalog",
                        principalTable: "catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attribute_definitions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_version = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    data_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    enum_options = table.Column<string>(type: "jsonb", nullable: true),
                    filterable = table.Column<bool>(type: "boolean", nullable: false),
                    searchable = table.Column<bool>(type: "boolean", nullable: false),
                    embeddable = table.Column<bool>(type: "boolean", nullable: false),
                    variant_axis = table.Column<bool>(type: "boolean", nullable: false),
                    display_group = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    validation = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definitions", x => x.id);
                    table.CheckConstraint("ck_attribute_definitions_data_type", "data_type IN ('string', 'text', 'integer', 'number', 'boolean', 'date', 'enum', 'multi_enum', 'json')");
                    table.CheckConstraint("ck_attribute_definitions_key", "key ~ '^[a-z][a-z0-9_]{0,63}$'");
                    table.ForeignKey(
                        name: "fk_attribute_definitions_product_type_id",
                        column: x => x.product_type_id,
                        principalSchema: "catalog",
                        principalTable: "product_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "category_closure",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ancestor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    descendant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_closure", x => new { x.tenant_id, x.ancestor_id, x.descendant_id });
                    table.ForeignKey(
                        name: "fk_category_closure_ancestor_id",
                        column: x => x.ancestor_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_category_closure_descendant_id",
                        column: x => x.descendant_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_version = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    search_tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('simple', search_text)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_attributes_object", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_products_attributes_size", "pg_column_size(attributes) <= 65536");
                    table.CheckConstraint("ck_products_status", "status IN ('draft', 'review', 'published', 'retired')");
                    table.CheckConstraint("ck_products_type_version", "type_version >= 1");
                    table.ForeignKey(
                        name: "fk_products_catalog_id",
                        column: x => x.catalog_id,
                        principalSchema: "catalog",
                        principalTable: "catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_products_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_products_product_type_id",
                        column: x => x.product_type_id,
                        principalSchema: "catalog",
                        principalTable: "product_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_documents",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rendered_text = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_documents_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_relationships",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_relationships", x => x.id);
                    table.CheckConstraint("ck_product_relationships_distinct", "source_id <> target_id");
                    table.CheckConstraint("ck_product_relationships_relationship_type", "relationship_type IN ('bundled_with', 'requires', 'compatible_with', 'alternative_to', 'upgrade_to', 'downgrade_to', 'upsell', 'cross_sell', 'replaces')");
                    table.ForeignKey(
                        name: "fk_product_relationships_source_id",
                        column: x => x.source_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_product_relationships_target_id",
                        column: x => x.target_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.CheckConstraint("ck_product_variants_attributes_object", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_product_variants_attributes_size", "pg_column_size(attributes) <= 65536");
                    table.CheckConstraint("ck_product_variants_status", "status IN ('active', 'inactive')");
                    table.ForeignKey(
                        name: "fk_product_variants_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_versions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    published_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_versions_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "availability",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    stock_qty = table.Column<int>(type: "integer", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_availability", x => x.id);
                    table.CheckConstraint("ck_availability_quantities", "(stock_qty IS NULL OR stock_qty >= 0) AND (capacity IS NULL OR capacity >= 0)");
                    table.CheckConstraint("ck_availability_status", "status IN ('active', 'inactive')");
                    table.CheckConstraint("ck_availability_target", "(product_id IS NULL) <> (variant_id IS NULL)");
                    table.CheckConstraint("ck_availability_validity", "valid_until IS NULL OR valid_until > valid_from");
                    table.ForeignKey(
                        name: "fk_availability_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_availability_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    s3_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media", x => x.id);
                    table.CheckConstraint("ck_media_kind", "kind IN ('image', 'video', 'document')");
                    table.ForeignKey(
                        name: "fk_media_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_media_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    billing_period = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    eligibility = table.Column<string>(type: "jsonb", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offers", x => x.id);
                    table.CheckConstraint("ck_offers_billing_period", "billing_period IN ('one_time', 'monthly', 'quarterly', 'annual')");
                    table.CheckConstraint("ck_offers_status", "status IN ('draft', 'active', 'inactive')");
                    table.CheckConstraint("ck_offers_target", "(product_id IS NULL) <> (variant_id IS NULL)");
                    table.CheckConstraint("ck_offers_validity", "valid_until IS NULL OR valid_until > valid_from");
                    table.ForeignKey(
                        name: "fk_offers_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_offers_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_attribute_index",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attribute_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    numeric_value = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    text_value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    bool_value = table.Column<bool>(type: "boolean", nullable: true),
                    date_value = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_attribute_index", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_attribute_index_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_product_attribute_index_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prices",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    price_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prices", x => x.id);
                    table.CheckConstraint("ck_prices_amount", "amount >= 0");
                    table.CheckConstraint("ck_prices_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_prices_price_type", "price_type IN ('list', 'promo', 'contract')");
                    table.CheckConstraint("ck_prices_status", "status IN ('active', 'inactive')");
                    table.CheckConstraint("ck_prices_validity", "valid_until IS NULL OR valid_until > valid_from");
                    table.ForeignKey(
                        name: "fk_prices_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definitions_product_type_id",
                schema: "catalog",
                table: "attribute_definitions",
                column: "product_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definitions_tenant_id_product_type_id_type_version_key",
                schema: "catalog",
                table: "attribute_definitions",
                columns: new[] { "tenant_id", "product_type_id", "type_version", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_occurred_at",
                schema: "catalog",
                table: "audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_availability_product_id",
                schema: "catalog",
                table: "availability",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_availability_tenant_id_product_id_region",
                schema: "catalog",
                table: "availability",
                columns: new[] { "tenant_id", "product_id", "region" });

            migrationBuilder.CreateIndex(
                name: "ix_availability_tenant_id_variant_id_region",
                schema: "catalog",
                table: "availability",
                columns: new[] { "tenant_id", "variant_id", "region" });

            migrationBuilder.CreateIndex(
                name: "ix_availability_variant_id",
                schema: "catalog",
                table: "availability",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_documents_product_id",
                schema: "catalog",
                table: "catalog_documents",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_documents_tenant_id_product_id_variant_id",
                schema: "catalog",
                table: "catalog_documents",
                columns: new[] { "tenant_id", "product_id", "variant_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_catalogs_tenant_id_key",
                schema: "catalog",
                table: "catalogs",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_catalog_id",
                schema: "catalog",
                table: "categories",
                column: "catalog_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id",
                schema: "catalog",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id_catalog_id_key",
                schema: "catalog",
                table: "categories",
                columns: new[] { "tenant_id", "catalog_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id_catalog_id_slug",
                schema: "catalog",
                table: "categories",
                columns: new[] { "tenant_id", "catalog_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id_parent_id",
                schema: "catalog",
                table: "categories",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_category_closure_ancestor_id",
                schema: "catalog",
                table: "category_closure",
                column: "ancestor_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_closure_descendant_id",
                schema: "catalog",
                table: "category_closure",
                column: "descendant_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_closure_tenant_id_descendant_id",
                schema: "catalog",
                table: "category_closure",
                columns: new[] { "tenant_id", "descendant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_media_product_id",
                schema: "catalog",
                table: "media",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_tenant_id_product_id",
                schema: "catalog",
                table: "media",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_media_variant_id",
                schema: "catalog",
                table: "media",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_offers_product_id",
                schema: "catalog",
                table: "offers",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_offers_tenant_id_product_id",
                schema: "catalog",
                table: "offers",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_tenant_id_variant_id",
                schema: "catalog",
                table: "offers",
                columns: new[] { "tenant_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_variant_id",
                schema: "catalog",
                table: "offers",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_created_at",
                schema: "catalog",
                table: "outbox",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_tenant_id_created_at",
                schema: "catalog",
                table: "outbox",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_prices_offer_id",
                schema: "catalog",
                table: "prices",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_prices_tenant_id_offer_id",
                schema: "catalog",
                table: "prices",
                columns: new[] { "tenant_id", "offer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_product_id",
                schema: "catalog",
                table: "product_attribute_index",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_tenant_id_attribute_key_bool_value",
                schema: "catalog",
                table: "product_attribute_index",
                columns: new[] { "tenant_id", "attribute_key", "bool_value" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_tenant_id_attribute_key_date_value",
                schema: "catalog",
                table: "product_attribute_index",
                columns: new[] { "tenant_id", "attribute_key", "date_value" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_tenant_id_attribute_key_numeric_value",
                schema: "catalog",
                table: "product_attribute_index",
                columns: new[] { "tenant_id", "attribute_key", "numeric_value" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_tenant_id_attribute_key_text_value",
                schema: "catalog",
                table: "product_attribute_index",
                columns: new[] { "tenant_id", "attribute_key", "text_value" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_tenant_id_product_id",
                schema: "catalog",
                table: "product_attribute_index",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_attribute_index_variant_id",
                schema: "catalog",
                table: "product_attribute_index",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_relationships_source_id",
                schema: "catalog",
                table: "product_relationships",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_relationships_target_id",
                schema: "catalog",
                table: "product_relationships",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_relationships_tenant_id_source_id_target_id_relationship_type",
                schema: "catalog",
                table: "product_relationships",
                columns: new[] { "tenant_id", "source_id", "target_id", "relationship_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_relationships_tenant_id_target_id",
                schema: "catalog",
                table: "product_relationships",
                columns: new[] { "tenant_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_types_tenant_id_key",
                schema: "catalog",
                table: "product_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id",
                schema: "catalog",
                table: "product_variants",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_tenant_id_catalog_id_sku",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "catalog_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_tenant_id_product_id",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_versions_product_id",
                schema: "catalog",
                table: "product_versions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_versions_tenant_id_product_id_version_number",
                schema: "catalog",
                table: "product_versions",
                columns: new[] { "tenant_id", "product_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_catalog_id",
                schema: "catalog",
                table: "products",
                column: "catalog_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id",
                schema: "catalog",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_product_type_id",
                schema: "catalog",
                table: "products",
                column: "product_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_search_tsv",
                schema: "catalog",
                table: "products",
                column: "search_tsv")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_catalog_id_sku",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "catalog_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_catalog_id_slug",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "catalog_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_catalog_id_status_published_at",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "catalog_id", "status", "published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_category_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_product_type_id_type_version",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "product_type_id", "type_version" });

            // Roles, grants, RLS, views, append-only triggers and the price EXCLUDE constraint (ADR-004).
            migrationBuilder.Sql(SecuritySql.Up());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.Down());

            migrationBuilder.DropTable(
                name: "attribute_definitions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "availability",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_documents",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "category_closure",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "consumer_inbox",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "media",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_attribute_index",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_relationships",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_versions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "offers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_types",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalogs",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "catalog");
        }
    }
}
