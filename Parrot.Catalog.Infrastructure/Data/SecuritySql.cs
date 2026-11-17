namespace Parrot.Catalog.Infrastructure.Data;

/// <summary>
/// The parts of the schema EF cannot express: roles and grants, Row Level Security,
/// the published-data views for the reader, append-only triggers and the price
/// EXCLUDE constraint. Kept as constants so the migration and the tests read the
/// same text (ADR-004).
/// </summary>
public static class SecuritySql
{
    public const string WriterRole = "catalog_writer";
    public const string ReaderRole = "catalog_reader";
    public const string RelayRole = "catalog_outbox_relay";

    public static readonly IReadOnlyList<string> Tables =
    [
        "tenants", "catalogs", "categories", "category_closure", "product_types", "attribute_definitions",
        "products", "product_variants", "product_attribute_index", "offers", "prices", "availability",
        "product_relationships", "media", "product_versions", "catalog_documents", "audit_log", "outbox", "consumer_inbox",
    ];

    /// <summary>
    /// Tables added after the first migration. Each gets the same tenant policy as
    /// <see cref="Tables"/>, in the migration that creates it (see <see cref="ItemAiStatusUp"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> LaterTables = ["item_ai_status", "stock_holds"];

    public static readonly IReadOnlyList<string> Views =
    [
        "v_catalogs", "v_categories", "v_category_closure", "v_product_types", "v_attribute_definitions",
        "v_products", "v_product_variants", "v_product_attribute_index", "v_offers", "v_prices", "v_availability",
        "v_product_relationships", "v_media", "v_catalog_documents",
    ];

    public static string Up()
    {
        List<string> sql = new List<string>
        {
            // The roles must already exist (bootstrap script / Terraform). Fail loudly otherwise.
            $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{WriterRole}') THEN
                    RAISE EXCEPTION 'role {WriterRole} must exist before migrating (see infra/catalog/bootstrap-roles.sql)';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ReaderRole}') THEN
                    RAISE EXCEPTION 'role {ReaderRole} must exist before migrating (see infra/catalog/bootstrap-roles.sql)';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RelayRole}') THEN
                    RAISE EXCEPTION 'role {RelayRole} must exist before migrating (see infra/catalog/bootstrap-roles.sql)';
                END IF;
            END $$;
            """,

            // Tenant from the session; a missing or empty setting matches no row.
            """
            CREATE OR REPLACE FUNCTION catalog.current_tenant_id() RETURNS uuid
            LANGUAGE sql STABLE PARALLEL SAFE AS $$
                SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
            $$;
            """,

            // Append-only: no UPDATE ever; DELETE only during an explicit purge.
            """
            CREATE OR REPLACE FUNCTION catalog.prevent_modification() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND current_setting('app.allow_purge', true) = 'on' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION '% is append-only (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
            END $$;
            """,
            "CREATE TRIGGER trg_audit_log_append_only BEFORE UPDATE OR DELETE ON catalog.audit_log FOR EACH ROW EXECUTE FUNCTION catalog.prevent_modification();",
            "CREATE TRIGGER trg_product_versions_append_only BEFORE UPDATE OR DELETE ON catalog.product_versions FOR EACH ROW EXECUTE FUNCTION catalog.prevent_modification();",

            // No two active prices for the same offer, currency and price type may overlap in time.
            """
            ALTER TABLE catalog.prices ADD CONSTRAINT ex_prices_no_overlap
                EXCLUDE USING gist (
                    offer_id WITH =,
                    currency WITH =,
                    price_type WITH =,
                    tstzrange(valid_from, COALESCE(valid_until, 'infinity'::timestamptz), '[)') WITH &&
                ) WHERE (status = 'active' AND deleted_at IS NULL);
            """,
        };

        foreach (string table in Tables)
        {
            sql.Add($"ALTER TABLE catalog.{table} ENABLE ROW LEVEL SECURITY;");
            sql.Add($"ALTER TABLE catalog.{table} FORCE ROW LEVEL SECURITY;");
            sql.Add($"CREATE POLICY tenant_isolation ON catalog.{table} FOR ALL USING (tenant_id = catalog.current_tenant_id()) WITH CHECK (tenant_id = catalog.current_tenant_id());");
        }

        sql.Add($"CREATE POLICY relay_all ON catalog.outbox FOR ALL TO {RelayRole} USING (true) WITH CHECK (true);");
        sql.Add($"CREATE POLICY relay_all ON catalog.consumer_inbox FOR ALL TO {RelayRole} USING (true) WITH CHECK (true);");

        sql.AddRange(ViewDefinitions());

        // Grants: writer = DML on tables (append-only on two of them, insert-only on outbox);
        // reader = SELECT on views only; relay = outbox + inbox only.
        sql.Add($"GRANT USAGE ON SCHEMA catalog TO {WriterRole}, {ReaderRole}, {RelayRole};");
        sql.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA catalog TO {WriterRole};");
        sql.Add($"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA catalog TO {WriterRole};");
        sql.Add($"REVOKE UPDATE, DELETE ON catalog.audit_log FROM {WriterRole};");
        sql.Add($"REVOKE UPDATE, DELETE ON catalog.product_versions FROM {WriterRole};");
        sql.Add($"REVOKE UPDATE, DELETE ON catalog.outbox FROM {WriterRole};");
        sql.Add($"REVOKE ALL ON catalog.consumer_inbox FROM {WriterRole};");
        foreach (string view in Views)
        {
            sql.Add($"REVOKE ALL ON catalog.{view} FROM {WriterRole};");
            sql.Add($"GRANT SELECT ON catalog.{view} TO {ReaderRole};");
        }

        sql.Add($"GRANT SELECT, UPDATE, DELETE ON catalog.outbox TO {RelayRole};");
        sql.Add($"GRANT SELECT, INSERT ON catalog.consumer_inbox TO {RelayRole};");
        sql.Add($"GRANT EXECUTE ON FUNCTION catalog.current_tenant_id() TO {WriterRole}, {ReaderRole}, {RelayRole};");

        return string.Join("\n", sql);
    }

    /// <summary>
    /// Security for item_ai_status: tenant isolation like every table; the writer keeps
    /// the "sent" side as it publishes; the relay records the AI's reports. The reader
    /// has no access: the status is for the business's own screens, not shoppers.
    /// </summary>
    public static string ItemAiStatusUp() => $"""
        ALTER TABLE catalog.item_ai_status ENABLE ROW LEVEL SECURITY;
        ALTER TABLE catalog.item_ai_status FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON catalog.item_ai_status FOR ALL USING (tenant_id = catalog.current_tenant_id()) WITH CHECK (tenant_id = catalog.current_tenant_id());
        CREATE POLICY relay_all ON catalog.item_ai_status FOR ALL TO {RelayRole} USING (true) WITH CHECK (true);
        GRANT SELECT, INSERT, UPDATE, DELETE ON catalog.item_ai_status TO {WriterRole};
        GRANT SELECT, UPDATE ON catalog.item_ai_status TO {RelayRole};
        """;

    public static string ItemAiStatusDown() => """
        DROP POLICY IF EXISTS relay_all ON catalog.item_ai_status;
        DROP POLICY IF EXISTS tenant_isolation ON catalog.item_ai_status;
        """;

    /// <summary>
    /// Security for stock_holds: tenant isolation like every table. The writer places,
    /// commits and releases holds; the reader sees only those still held (v_stock_holds),
    /// to take them off what's left when it answers about stock.
    /// </summary>
    public static string StockHoldsUp() => $"""
        ALTER TABLE catalog.stock_holds ENABLE ROW LEVEL SECURITY;
        ALTER TABLE catalog.stock_holds FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON catalog.stock_holds FOR ALL USING (tenant_id = catalog.current_tenant_id()) WITH CHECK (tenant_id = catalog.current_tenant_id());
        GRANT SELECT, INSERT, UPDATE ON catalog.stock_holds TO {WriterRole};
        CREATE VIEW catalog.v_stock_holds AS SELECT h.* FROM catalog.stock_holds h WHERE h.status = 'held';
        GRANT SELECT ON catalog.v_stock_holds TO {ReaderRole};
        """;

    public static string StockHoldsDown() => """
        DROP VIEW IF EXISTS catalog.v_stock_holds;
        DROP POLICY IF EXISTS tenant_isolation ON catalog.stock_holds;
        """;

    /// <summary>
    /// Re-creates views over a table that gained columns. A view's <c>SELECT *</c> is expanded
    /// when the view is created, so without this the reader never sees the new columns.
    /// </summary>
    public static string ReplaceViews(params string[] views)
    {
        List<string> sql = new List<string>();
        foreach (string view in views)
        {
            string definition = LatestViewDefinitions().Single(d => d.Contains($"catalog.{view} AS", StringComparison.Ordinal));
            sql.Add(definition.Replace("CREATE VIEW", "CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        }

        return string.Join("\n", sql);
    }

    public static string Down()
    {
        List<string> sql = new List<string>();
        foreach (string view in Views.Reverse())
        {
            sql.Add($"DROP VIEW IF EXISTS catalog.{view};");
        }

        sql.Add("ALTER TABLE catalog.prices DROP CONSTRAINT IF EXISTS ex_prices_no_overlap;");
        sql.Add("DROP TRIGGER IF EXISTS trg_audit_log_append_only ON catalog.audit_log;");
        sql.Add("DROP TRIGGER IF EXISTS trg_product_versions_append_only ON catalog.product_versions;");
        foreach (string table in Tables)
        {
            sql.Add($"DROP POLICY IF EXISTS tenant_isolation ON catalog.{table};");
        }

        sql.Add("DROP POLICY IF EXISTS relay_all ON catalog.outbox;");
        sql.Add("DROP POLICY IF EXISTS relay_all ON catalog.consumer_inbox;");
        sql.Add("DROP FUNCTION IF EXISTS catalog.prevent_modification();");
        sql.Add("DROP FUNCTION IF EXISTS catalog.current_tenant_id();");
        return string.Join("\n", sql);
    }

    /// <summary>
    /// v_products as it stands now: published, not deleted and not hidden. A hidden item is
    /// kept but not offered, so no reader (and no AI tool) may see it. The first migration
    /// builds the views before products had a hidden column, so it keeps the original.
    /// </summary>
    public const string ProductsView =
        "CREATE VIEW catalog.v_products AS SELECT * FROM catalog.products p WHERE p.status = 'published' AND p.deleted_at IS NULL AND p.hidden = false;";

    /// <summary>v_products before hidden items were kept from readers; what ReadersSkipHidden's Down puts back.</summary>
    public const string ProductsViewBeforeHidden =
        "CREATE VIEW catalog.v_products AS SELECT * FROM catalog.products p WHERE p.status = 'published' AND p.deleted_at IS NULL;";

    private static IEnumerable<string> LatestViewDefinitions()
        => ViewDefinitions().Select(d => d == ProductsViewBeforeHidden ? ProductsView : d);

    // Views run with the owner's privileges, and the owner is under FORCE RLS, so the
    // reader sees only its own tenant's published rows while holding no grant on any table.
    private static IEnumerable<string> ViewDefinitions()
    {
        yield return "CREATE VIEW catalog.v_catalogs AS SELECT * FROM catalog.catalogs WHERE deleted_at IS NULL;";
        yield return "CREATE VIEW catalog.v_categories AS SELECT * FROM catalog.categories WHERE deleted_at IS NULL;";
        yield return "CREATE VIEW catalog.v_category_closure AS SELECT * FROM catalog.category_closure;";
        yield return "CREATE VIEW catalog.v_product_types AS SELECT * FROM catalog.product_types;";
        yield return "CREATE VIEW catalog.v_attribute_definitions AS SELECT * FROM catalog.attribute_definitions;";
        yield return ProductsViewBeforeHidden;
        yield return """
            CREATE VIEW catalog.v_product_variants AS
                SELECT v.* FROM catalog.product_variants v
                WHERE v.deleted_at IS NULL AND v.status = 'active'
                  AND EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = v.product_id);
            """;
        yield return """
            CREATE VIEW catalog.v_product_attribute_index AS
                SELECT i.* FROM catalog.product_attribute_index i
                WHERE EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = i.product_id)
                  AND (i.variant_id IS NULL OR EXISTS (SELECT 1 FROM catalog.v_product_variants v WHERE v.id = i.variant_id));
            """;
        yield return """
            CREATE VIEW catalog.v_offers AS
                SELECT o.* FROM catalog.offers o
                WHERE o.deleted_at IS NULL AND o.status <> 'draft'
                  AND ((o.product_id IS NOT NULL AND EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = o.product_id))
                    OR (o.variant_id IS NOT NULL AND EXISTS (SELECT 1 FROM catalog.v_product_variants v WHERE v.id = o.variant_id)));
            """;
        yield return """
            CREATE VIEW catalog.v_prices AS
                SELECT pr.* FROM catalog.prices pr
                WHERE pr.deleted_at IS NULL AND EXISTS (SELECT 1 FROM catalog.v_offers o WHERE o.id = pr.offer_id);
            """;
        yield return """
            CREATE VIEW catalog.v_availability AS
                SELECT a.* FROM catalog.availability a
                WHERE a.deleted_at IS NULL
                  AND ((a.product_id IS NOT NULL AND EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = a.product_id))
                    OR (a.variant_id IS NOT NULL AND EXISTS (SELECT 1 FROM catalog.v_product_variants v WHERE v.id = a.variant_id)));
            """;
        yield return """
            CREATE VIEW catalog.v_product_relationships AS
                SELECT r.* FROM catalog.product_relationships r
                WHERE r.deleted_at IS NULL
                  AND EXISTS (SELECT 1 FROM catalog.v_products s WHERE s.id = r.source_id)
                  AND EXISTS (SELECT 1 FROM catalog.v_products t WHERE t.id = r.target_id);
            """;
        yield return """
            CREATE VIEW catalog.v_media AS
                SELECT m.* FROM catalog.media m
                WHERE m.deleted_at IS NULL AND EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = m.product_id);
            """;
        yield return """
            CREATE VIEW catalog.v_catalog_documents AS
                SELECT d.* FROM catalog.catalog_documents d
                WHERE EXISTS (SELECT 1 FROM catalog.v_products p WHERE p.id = d.product_id);
            """;
    }
}
