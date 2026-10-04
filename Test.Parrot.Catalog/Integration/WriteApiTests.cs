using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Domain.Enums;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

[Collection(CatalogApiCollection.Name)]
public class WriteApiTests
{
    private readonly CatalogApiFixture _fx;

    public WriteApiTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task Attribute_validation_rejects_unknown_wrong_missing_and_misplaced_keys()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Write);
        Guid catalog = _fx.SeedA!.CatalogId;
        Guid laptopType = _fx.SeedA.TypeIds["laptop"];

        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-1", """{"brand":"P","price":10}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-2", """{"brand":"P","screen_inches":"big"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-3", """{"screen_inches":14}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-4", """{"brand":"P","ram_gb":16}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-5", """{"brand":"P","screen_inches":99}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "bad sku!", """{"brand":"P"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(client, catalog, laptopType, "X-6", "{\"brand\":\"" + new string('a', 70_000) + "\"}"));
        Assert.Equal(HttpStatusCode.Created, await Create(client, catalog, laptopType, "X-OK", """{"brand":"P","screen_inches":15.6}"""));
        Assert.Equal(HttpStatusCode.Conflict, await Create(client, catalog, laptopType, "X-OK", """{"brand":"P"}"""));

        // Unknown top-level JSON members are rejected by the strict contract, not ignored.
        HttpResponseMessage unknown = await client.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","product_type_id":"{{laptopType}}","sku":"X-7","name":"n","attributes":{"brand":"P"},"tenant_id":"{{Guid.NewGuid()}}" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [PostgresFact]
    public async Task Publish_is_one_transaction_with_version_document_index_and_outbox()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Write, CatalogScopes.Publish);
        Guid catalog = _fx.SeedA!.CatalogId;
        Guid consultingType = _fx.SeedA.TypeIds["consulting"];

        HttpResponseMessage created = await client.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","product_type_id":"{{consultingType}}","sku":"SVC-PUB","name":"Publish me","attributes":{"duration_days":5,"includes_training":false} }"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ProductAdminResponse product = await CatalogApiFixture.ReadAsync<ProductAdminResponse>(created);
        Assert.Equal(ProductStatus.Draft, product.Status);

        // Not visible on the read side while draft.
        using HttpClient reader = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/catalog/products/{product.Id}")).StatusCode);

        HttpResponseMessage published = await client.PostAsync($"/api/catalog/admin/products/{product.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        ProductAdminResponse after = await CatalogApiFixture.ReadAsync<ProductAdminResponse>(published);
        Assert.Equal(ProductStatus.Published, after.Status);
        Assert.Equal(1, after.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/catalog/admin/products/{product.Id}/publish", null)).StatusCode);

        long versions = await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.product_versions WHERE product_id = @id", new NpgsqlParameter("id", product.Id));
        long documents = await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.catalog_documents WHERE product_id = @id", new NpgsqlParameter("id", product.Id));
        long indexRows = await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.product_attribute_index WHERE product_id = @id", new NpgsqlParameter("id", product.Id));
        long events = await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.outbox WHERE aggregate_id = @id AND event_type = 'ProductPublished'", new NpgsqlParameter("id", product.Id));
        long audits = await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.audit_log WHERE entity_id = @id AND action = 'publish'", new NpgsqlParameter("id", product.Id.ToString("D")));
        Assert.Equal(1, versions);
        Assert.Equal(1, documents);
        Assert.Equal(2, indexRows);
        Assert.Equal(1, events);
        Assert.Equal(1, audits);

        // Now visible on the read side.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/catalog/products/{product.Id}")).StatusCode);

        // Editing a published product versions it again and rebuilds the index.
        HttpResponseMessage updated = await client.PutAsync($"/api/catalog/admin/products/{product.Id}", CatalogApiFixture.Raw("""{"name":"Publish me v2","attributes":{"duration_days":7,"includes_training":true,"support_months":1}}"""));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await CatalogApiFixture.ReadAsync<ProductAdminResponse>(updated)).Version);
        Assert.Equal(3, await _fx.Database.ScalarAsync<long>(_fx.Database.Migrator, _fx.TenantA, "SELECT count(*) FROM catalog.product_attribute_index WHERE product_id = @id", new NpgsqlParameter("id", product.Id)));

        // Retire, then it is frozen.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/catalog/admin/products/{product.Id}/retire", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/catalog/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/api/catalog/admin/products/{product.Id}", CatalogApiFixture.Raw("""{"name":"x","attributes":{"duration_days":7,"includes_training":true}}"""))).StatusCode);
    }

    [PostgresFact]
    public async Task Overlapping_active_prices_are_refused_by_the_database()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Write);
        Guid fibre = _fx.SeedA!.ProductIds["internet_plan"];
        IReadOnlyList<OfferAdminResponse> offers = await CatalogApiFixture.ReadAsync<IReadOnlyList<OfferAdminResponse>>(await client.GetAsync($"/api/catalog/admin/products/{fibre}/offers"));
        OfferAdminResponse pro = offers.Single(o => o.Name == "Pro");

        HttpResponseMessage overlap = await client.PostAsync($"/api/catalog/admin/offers/{pro.Id}/prices", CatalogApiFixture.Raw("""{"amount": 47000, "currency": "NGN", "price_type": "list", "valid_from": "2026-06-01T00:00:00Z"}"""));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

        HttpResponseMessage promo = await client.PostAsync($"/api/catalog/admin/offers/{pro.Id}/prices", CatalogApiFixture.Raw("""{"amount": 39000, "currency": "NGN", "price_type": "promo", "valid_from": "2026-06-01T00:00:00Z", "valid_until": "2026-07-01T00:00:00Z"}"""));
        Assert.Equal(HttpStatusCode.Created, promo.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/catalog/admin/offers/{pro.Id}/prices", CatalogApiFixture.Raw("""{"amount": 1, "currency": "XXX", "price_type": "list", "valid_from": "2027-01-01T00:00:00Z"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/catalog/admin/offers/{pro.Id}/prices", CatalogApiFixture.Raw("""{"amount": -1, "currency": "NGN", "price_type": "list", "valid_from": "2027-01-01T00:00:00Z"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/admin/offers", CatalogApiFixture.Raw($$"""{"product_id":"{{fibre}}","variant_id":"{{Guid.NewGuid()}}","name":"both","billing_period":"monthly","valid_from":"2026-01-01T00:00:00Z"}"""))).StatusCode);
    }

    [PostgresFact]
    public async Task Audit_log_and_versions_are_append_only_for_the_writer_role()
    {
        PostgresException update = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ExecAsync(_fx.Database.Writer, _fx.TenantA, "UPDATE catalog.audit_log SET actor = 'x'"));
        Assert.Equal("42501", update.SqlState);
        PostgresException delete = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ExecAsync(_fx.Database.Writer, _fx.TenantA, "DELETE FROM catalog.product_versions"));
        Assert.Equal("42501", delete.SqlState);

        // Even the owner is stopped by the trigger unless purging.
        PostgresException owner = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ExecAsync(_fx.Database.Migrator, _fx.TenantA, "UPDATE catalog.audit_log SET actor = 'x'"));
        Assert.Equal("42501", owner.SqlState);
    }

    [PostgresFact]
    public async Task Type_versions_pin_products_and_migration_dry_run_reports_without_changing()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Admin);
        HttpResponseMessage created = await client.PostAsync("/api/catalog/admin/types", CatalogApiFixture.Raw("""{"key":"widget","name":"Widget","kind":"good","attributes":[{"key":"weight_kg","label":"Weight","data_type":"number","unit":"kg","filterable":true,"required":true},{"key":"legacy","label":"Legacy","data_type":"string"}]}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ProductTypeResponse widget = await CatalogApiFixture.ReadAsync<ProductTypeResponse>(created);

        HttpResponseMessage product = await client.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw($$"""{"catalog_id":"{{_fx.SeedA!.CatalogId}}","product_type_id":"{{widget.Id}}","sku":"WDG-1","name":"Widget one","attributes":{"weight_kg":1.5,"legacy":"old"} }"""));
        Assert.Equal(HttpStatusCode.Created, product.StatusCode);
        ProductAdminResponse p = await CatalogApiFixture.ReadAsync<ProductAdminResponse>(product);
        Assert.Equal(1, p.Type.Version);

        // data_type change inside a version is refused; it needs a new version.
        AttributeDefinitionResponse weight = widget.Attributes.Single(a => a.Key == "weight_kg");
        HttpResponseMessage v2 = await client.PostAsync($"/api/catalog/admin/types/{widget.Id}/versions", CatalogApiFixture.Raw("""{"remove":["legacy"],"change":[{"key":"weight_kg","label":"Weight","data_type":"integer","unit":"kg","filterable":true,"required":true}],"add":[{"key":"colour","label":"Colour","data_type":"enum","enum_options":["red","blue"],"required":true}]}"""));
        Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        Assert.Equal(2, (await CatalogApiFixture.ReadAsync<ProductTypeResponse>(v2)).Version);

        HttpResponseMessage schema = await client.GetAsync($"/api/catalog/admin/types/{widget.Id}/schema");
        TypeSchemaResponse schemaBody = await CatalogApiFixture.ReadAsync<TypeSchemaResponse>(schema);
        Assert.Equal(2, schemaBody.Version);
        Assert.True(schemaBody.ProductSchema.GetProperty("properties").TryGetProperty("colour", out _));
        Assert.False(schemaBody.ProductSchema.GetProperty("properties").TryGetProperty("legacy", out _));

        // The existing product still pins version 1 and still validates against it.
        ProductAdminResponse still = await CatalogApiFixture.ReadAsync<ProductAdminResponse>(await client.GetAsync($"/api/catalog/admin/products/{p.Id}"));
        Assert.Equal(1, still.Type.Version);

        using (var scope = _fx.Scope(_fx.TenantA))
        {
            ITypeVersionMigrator migrator = scope.ServiceProvider.GetRequiredService<ITypeVersionMigrator>();
            TypeVersionMigrationReport dry = await migrator.MigrateAsync(widget.Id, 1, 2, dryRun: true, CancellationToken.None);
            Assert.True(dry.DryRun);
            Assert.Equal(1, dry.Total);
            Assert.Equal(1, dry.Failed); // weight 1.5 is not an integer and colour is required
            Assert.Contains("legacy", dry.Items[0].DroppedKeys);
            Assert.Equal(0, dry.Migrated);
        }

        Assert.Equal(1, (await CatalogApiFixture.ReadAsync<ProductAdminResponse>(await client.GetAsync($"/api/catalog/admin/products/{p.Id}"))).Type.Version);

        // Fix the product on v1 so it is migratable, then migrate for real.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/catalog/admin/products/{p.Id}", CatalogApiFixture.Raw("""{"name":"Widget one","attributes":{"weight_kg":2}}"""))).StatusCode);
        using (var scope = _fx.Scope(_fx.TenantA))
        {
            ITypeVersionMigrator migrator = scope.ServiceProvider.GetRequiredService<ITypeVersionMigrator>();
            TypeVersionMigrationReport dry = await migrator.MigrateAsync(widget.Id, 1, 2, dryRun: true, CancellationToken.None);
            Assert.Equal(1, dry.Failed); // colour still required
        }

        HttpResponseMessage relaxed = await client.PostAsync($"/api/catalog/admin/types/{widget.Id}/versions", CatalogApiFixture.Raw("""{"change":[{"key":"colour","label":"Colour","data_type":"enum","enum_options":["red","blue"],"required":false}]}"""));
        Assert.Equal(HttpStatusCode.OK, relaxed.StatusCode);
        using (var scope = _fx.Scope(_fx.TenantA))
        {
            ITypeVersionMigrator migrator = scope.ServiceProvider.GetRequiredService<ITypeVersionMigrator>();
            TypeVersionMigrationReport real = await migrator.MigrateAsync(widget.Id, 1, 3, dryRun: false, CancellationToken.None);
            Assert.Equal(1, real.Migrated);
        }

        Assert.Equal(3, (await CatalogApiFixture.ReadAsync<ProductAdminResponse>(await client.GetAsync($"/api/catalog/admin/products/{p.Id}"))).Type.Version);
    }

    [PostgresFact]
    public async Task Categories_maintain_a_closure_table_and_refuse_cycles()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Write);
        Guid catalog = _fx.SeedA!.CatalogId;
        CategoryResponse root = await CatalogApiFixture.ReadAsync<CategoryResponse>(await client.PostAsync("/api/catalog/admin/categories", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","key":"root_x","name":"Root X"}""")));
        CategoryResponse child = await CatalogApiFixture.ReadAsync<CategoryResponse>(await client.PostAsync("/api/catalog/admin/categories", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","parent_id":"{{root.Id}}","key":"child_x","name":"Child X"}""")));
        CategoryResponse grandchild = await CatalogApiFixture.ReadAsync<CategoryResponse>(await client.PostAsync("/api/catalog/admin/categories", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","parent_id":"{{child.Id}}","key":"grand_x","name":"Grand X"}""")));
        Assert.Equal(["Root X", "Child X", "Grand X"], grandchild.Path);

        HttpResponseMessage cycle = await client.PutAsync($"/api/catalog/admin/categories/{root.Id}", CatalogApiFixture.Raw($$"""{"parent_id":"{{grandchild.Id}}","name":"Root X","sort_order":0}"""));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        // Re-parent the child under hardware: the grandchild's path follows.
        HttpResponseMessage moved = await client.PutAsync($"/api/catalog/admin/categories/{child.Id}", CatalogApiFixture.Raw($$"""{"parent_id":"{{_fx.SeedA.CategoryIds["hardware"]}}","name":"Child X","sort_order":0}"""));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        CategoryResponse grandAfter = await CatalogApiFixture.ReadAsync<CategoryResponse>(await client.GetAsync($"/api/catalog/admin/categories/{grandchild.Id}"));
        Assert.Equal(["Hardware", "Child X", "Grand X"], grandAfter.Path);

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/catalog/admin/categories/{child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/catalog/admin/categories/{grandchild.Id}")).StatusCode);
    }

    private static async Task<HttpStatusCode> Create(HttpClient client, Guid catalog, Guid type, string sku, string attributes)
    {
        HttpResponseMessage response = await client.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw($$"""{"catalog_id":"{{catalog}}","product_type_id":"{{type}}","sku":"{{sku}}","name":"Test {{sku}}","attributes":{{attributes}} }"""));
        return response.StatusCode;
    }
}
