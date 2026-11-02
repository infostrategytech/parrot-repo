using System.Net;
using System.Text.Json;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Domain.Enums;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

/// <summary>
/// The dashboard's items routes. Each test works in a fresh workspace so the catalog and
/// its two plain types are created on first use, as they are for a new workspace.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class ItemsApiTests
{
    private static readonly string[] Owner = [CatalogScopes.Read, CatalogScopes.Write, CatalogScopes.Publish];

    private readonly CatalogApiFixture _fx;

    public ItemsApiTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task A_live_item_is_saved_whole_and_the_ai_reads_its_unit_and_lead_time()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);

        HttpResponseMessage created = await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw("""
            {"name":"Adire dyeing class","kind":"service","description":"Two hours, all materials included.",
             "pricing":{"mode":"single","amount":20000,"per":"person"},
             "availability":{"mode":"made_to_order","lead_time_days":5},
             "status":"published"}
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(created);
        Assert.Equal(ItemKind.Service, item.Kind);
        Assert.Equal(ProductStatus.Published, item.Status);
        Assert.Equal(PricingMode.Single, item.Pricing.Mode);
        Assert.Equal(20000m, item.Pricing.Amount);
        Assert.Equal("person", item.Pricing.Per);
        Assert.Equal("NGN", item.Pricing.Currency);
        Assert.Equal(new ItemAvailability(AvailabilityMode.MadeToOrder, true, null, 5), item.Availability);

        // Live means the read API and the AI tools see it, with the new fields.
        using HttpClient reader = _fx.Client(workspace, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/catalog/products/{item.Id}")).StatusCode);

        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        JsonElement offers = await Result(await ai.PostAsync("/api/catalog/ai/tools/get_product_offers", CatalogApiFixture.Body(new { id = item.Id })));
        JsonElement offer = offers.GetProperty("offers")[0];
        Assert.Equal("person", offer.GetProperty("unit").GetString());
        Assert.False(offer.GetProperty("price_on_request").GetBoolean());
        Assert.Equal(20000m, offer.GetProperty("prices")[0].GetProperty("amount").GetDecimal());

        JsonElement availability = await Result(await ai.PostAsync("/api/catalog/ai/tools/get_product_availability", CatalogApiFixture.Body(new { id = item.Id })));
        JsonElement row = availability.GetProperty("availability")[0];
        Assert.Equal("made_to_order", row.GetProperty("mode").GetString());
        Assert.Equal(5, row.GetProperty("lead_time_days").GetInt32());
    }

    [PostgresFact]
    public async Task Pricing_moves_between_variants_on_request_and_a_single_price()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);

        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw("""
            {"name":"Indigo adire kaftan","kind":"product",
             "pricing":{"mode":"variants","variants":[{"name":"Small","amount":25000},{"name":"Large","amount":60000}]},
             "availability":{"mode":"in_stock","quantity":4},"status":"published"}
            """)));
        Assert.Equal(PricingMode.Variants, item.Pricing.Mode);
        Assert.Equal(["Small", "Large"], item.Pricing.Variants.Select(v => v.Name));
        Assert.Equal(25000m, item.Pricing.MinAmount);
        Assert.Equal(60000m, item.Pricing.MaxAmount);

        // Keep Small with a new price, drop Large, add Medium.
        Guid small = item.Pricing.Variants[0].Id;
        HttpResponseMessage edited = await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw($$"""
            {"name":"Indigo adire kaftan","kind":"product",
             "pricing":{"mode":"variants","variants":[{"id":"{{small}}","name":"Small","amount":27000},{"name":"Medium","amount":40000}]},
             "availability":{"mode":"in_stock","quantity":4},"status":"published"}
            """));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        item = await CatalogApiFixture.ReadAsync<ItemResponse>(edited);
        Assert.Equal([(small, "Small", (decimal?)27000m)], item.Pricing.Variants.Take(1).Select(v => (v.Id, v.Name, v.Amount)));
        Assert.Equal(["Small", "Medium"], item.Pricing.Variants.Select(v => v.Name));

        // Price on request: the variants go and the AI is told to quote on request.
        item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw("""
            {"name":"Indigo adire kaftan","kind":"product","pricing":{"mode":"on_request"},
             "availability":{"mode":"in_stock"},"status":"published"}
            """)));
        Assert.Equal(PricingMode.OnRequest, item.Pricing.Mode);
        Assert.Empty(item.Pricing.Variants);

        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        JsonElement offers = (await Result(await ai.PostAsync("/api/catalog/ai/tools/get_product_offers", CatalogApiFixture.Body(new { id = item.Id })))).GetProperty("offers");
        Assert.Equal(1, offers.GetArrayLength());
        Assert.True(offers[0].GetProperty("price_on_request").GetBoolean());
        Assert.Equal(0, offers[0].GetProperty("prices").GetArrayLength());

        // And back to one price on the same offer.
        item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw("""
            {"name":"Indigo adire kaftan","kind":"product","pricing":{"mode":"single","amount":45000},
             "availability":{"mode":"in_stock"},"status":"published"}
            """)));
        Assert.Equal(PricingMode.Single, item.Pricing.Mode);
        Assert.Equal(45000m, item.Pricing.Amount);
        Assert.Null(item.Pricing.Per);
    }

    [PostgresFact]
    public async Task Drafts_stay_off_the_read_side_and_going_live_needs_publish()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient writer = _fx.Client(workspace, CatalogScopes.Read, CatalogScopes.Write);
        const string Draft = """
            {"name":"Beaded evening clutch","kind":"product","pricing":{"mode":"single","amount":27000},
             "availability":{"mode":"in_stock","quantity":1},"status":"draft"}
            """;

        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Draft.Replace("\"draft\"", "\"published\"")))).StatusCode);
        HttpResponseMessage created = await writer.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Draft));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(created);
        Assert.Equal(ProductStatus.Draft, item.Status);

        using HttpClient reader = _fx.Client(workspace, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/catalog/products/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/catalog/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Draft))).StatusCode);

        using HttpClient owner = _fx.Client(workspace, Owner);
        ItemResponse live = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw(Draft.Replace("\"draft\"", "\"published\""))));
        Assert.Equal(ProductStatus.Published, live.Status);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/catalog/products/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw(Draft))).StatusCode);
    }

    [PostgresFact]
    public async Task The_list_filters_by_tab_category_and_search_and_counts_each_tab()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);

        HttpResponseMessage newCategory = await owner.PostAsync("/api/catalog/categories", CatalogApiFixture.Body(new CreateItemCategoryRequest("Clothing")));
        Assert.Equal(HttpStatusCode.Created, newCategory.StatusCode);
        ItemCategory clothing = await CatalogApiFixture.ReadAsync<ItemCategory>(newCategory);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/catalog/categories", CatalogApiFixture.Body(new CreateItemCategoryRequest("clothing")))).StatusCode);
        Assert.Equal([clothing], await CatalogApiFixture.ReadAsync<List<ItemCategory>>(await owner.GetAsync("/api/catalog/categories")));

        await Add(owner, "Adire scarf", """{"mode":"in_stock","quantity":12}""", "published", clothing.Id);
        ItemResponse soldOut = await Add(owner, "Adire tote bag", """{"mode":"in_stock","quantity":0}""", "published");
        await Add(owner, "Made-to-measure tailoring", """{"mode":"made_to_order","available":false}""", "published", clothing.Id);
        await Add(owner, "Bust adire two-piece", """{"mode":"in_stock"}""", "draft", clothing.Id);

        ItemListResponse all = await List(owner, "");
        Assert.Equal(new ItemCounts(4, 1, 2, 1), all.Counts);
        Assert.Equal(4, all.Total);
        Assert.Equal("Bust adire two-piece", all.Items[0].Name);

        Assert.Equal(["Adire scarf"], (await List(owner, "?status=available")).Items.Select(i => i.Name));
        Assert.Equal(["Made-to-measure tailoring", "Adire tote bag"], (await List(owner, "?status=unavailable")).Items.Select(i => i.Name));
        Assert.Equal(["Bust adire two-piece"], (await List(owner, "?status=draft")).Items.Select(i => i.Name));

        ItemListResponse inClothing = await List(owner, $"?category_id={clothing.Id}");
        Assert.Equal(new ItemCounts(3, 1, 1, 1), inClothing.Counts);
        Assert.All(inClothing.Items, i => Assert.Equal(clothing, i.Category));

        Assert.Equal(["Bust adire two-piece", "Adire tote bag", "Adire scarf"], (await List(owner, "?q=ADIRE")).Items.Select(i => i.Name));
        Assert.Equal(["Adire scarf"], (await List(owner, "?q=adire%20s")).Items.Select(i => i.Name));
        Assert.Empty((await List(owner, "?q=50%25")).Items);

        ItemListResponse paged = await List(owner, "?page=2&page_size=3");
        Assert.Equal(4, paged.Total);
        Assert.Equal(["Adire scarf"], paged.Items.Select(i => i.Name));

        // Restocking from the list's dropdown moves it to Available.
        HttpResponseMessage restocked = await owner.PatchAsync($"/api/catalog/items/{soldOut.Id}/availability", CatalogApiFixture.Raw("""{"mode":"in_stock","quantity":3}"""));
        Assert.Equal(HttpStatusCode.OK, restocked.StatusCode);
        Assert.Equal(new ItemCounts(4, 2, 1, 1), (await List(owner, "")).Counts);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/catalog/items/{soldOut.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/catalog/items/{soldOut.Id}")).StatusCode);
        Assert.Equal(3, (await List(owner, "")).Total);
    }

    [PostgresFact]
    public async Task Invalid_forms_are_refused_with_a_reason()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);

        string Form(string pricing, string availability = """{"mode":"in_stock"}""", string kind = "product")
            => $$"""{"name":"Thing","kind":"{{kind}}","pricing":{{pricing}},"availability":{{availability}},"status":"published"}""";

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single"}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":-1}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"variants"}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"variants","amount":5,"variants":[{"name":"A","amount":5}]}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"on_request","amount":5}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5,"currency":"naira"}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5}""", """{"mode":"bookings","quantity":3}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5}""", """{"mode":"in_stock","lead_time_days":3}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5,"surprise":1}""")))).StatusCode);

        // Nothing half-saved: every refusal above left the workspace empty.
        Assert.Equal(0, (await List(owner, "")).Total);

        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5}"""))));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5}""", kind: "service")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw(Form($$"""{"mode":"variants","variants":[{"id":"{{Guid.NewGuid()}}","name":"A","amount":5}]}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsync($"/api/catalog/items/{Guid.NewGuid()}", CatalogApiFixture.Raw(Form("""{"mode":"single","amount":5}""")))).StatusCode);
    }

    [PostgresFact]
    public async Task Another_workspace_cannot_see_or_change_an_item()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        ItemResponse item = await Add(owner, "Indigo cushion cover", """{"mode":"in_stock","quantity":2}""", "published");

        using HttpClient other = _fx.Client(Guid.NewGuid(), Owner);
        Assert.Equal(0, (await List(other, "")).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/catalog/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Raw("""
            {"name":"hijacked","kind":"product","pricing":{"mode":"single","amount":1},"availability":{"mode":"in_stock"},"status":"published"}
            """))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PatchAsync($"/api/catalog/items/{item.Id}/availability", CatalogApiFixture.Raw("""{"mode":"in_stock","available":false}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/catalog/items/{item.Id}")).StatusCode);

        ItemResponse unchanged = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.GetAsync($"/api/catalog/items/{item.Id}"));
        Assert.Equal("Indigo cushion cover", unchanged.Name);
        Assert.True(unchanged.Availability!.Available);
    }

    private static async Task<ItemResponse> Add(HttpClient client, string name, string availability, string status, Guid? categoryId = null)
    {
        string category = categoryId is { } id ? $",\"category_id\":\"{id}\"" : string.Empty;
        HttpResponseMessage response = await client.PostAsync("/api/catalog/items", CatalogApiFixture.Raw(
            $$"""{"name":"{{name}}","kind":"product","pricing":{"mode":"single","amount":1000},"availability":{{availability}},"status":"{{status}}"{{category}}}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await CatalogApiFixture.ReadAsync<ItemResponse>(response);
    }

    private static async Task<ItemListResponse> List(HttpClient client, string query)
    {
        HttpResponseMessage response = await client.GetAsync("/api/catalog/items" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await CatalogApiFixture.ReadAsync<ItemListResponse>(response);
    }

    private static async Task<JsonElement> Result(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").Clone();
    }
}
