using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Catalog;
using Parrot.Web.Features.Dashboard.Catalog.ConceptE;

namespace Test.Parrot.Api.Tests.Web.Catalog;

// Products & Services against the catalog service: what the store sends for the form, and how it reads items back.
public class ApiCatalogStoreTests
{
    private const string Variant = "0b3c8c43-6a2d-4c55-9d4c-3d6c1f6a0a01";
    private const string ItemId = "7d1f2c1e-0f9a-4c3b-8d6e-2a5b4c3d2e1f";

    [Theory]
    [InlineData("USD")]
    [InlineData("GBP")]
    [InlineData("GHS")]
    [InlineData("NGN")]
    public async Task PricingCurrencyUsesTheExistingCatalogContractOnWriteAndRead(string currency)
    {
        var catalog = new FakeCatalog();
        catalog.Respond("GET api/catalog/categories", "[]");
        catalog.Respond("POST api/catalog/items", Item($$"""
            "status":"live","pricing":{"mode":"single","currency":"{{currency}}","amount":25.50,"variants":[]}
            """));
        var saved = await Store(catalog).CreateAsync(new CatalogEntry { Name = "Controlled item", Price = 25.50m, CurrencyCode = currency });
        Assert.Equal(currency, catalog.Bodies["POST api/catalog/items"].GetProperty("pricing").GetProperty("currency").GetString());
        Assert.Equal(currency, saved.CurrencyCode); Assert.Equal(25.50m, saved.Price);
    }

    [Fact]
    public async Task A_new_item_uploads_its_picked_photo_first_and_saves_the_form()
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items/photos", """{"key":"tenants/t/items/a.jpg","url":"http://photos/a.jpg"}""");
        catalog.Respond("GET api/catalog/categories", "[]");
        catalog.Respond("POST api/catalog/categories", """{"id":"5a0c6f1e-6d2b-4d8e-9a3c-1b2c3d4e5f60","name":"Dresses"}""");
        catalog.Respond("POST api/catalog/items", Item("""
            "status":"draft","pricing":{"mode":"variants","currency":"NGN","variants":[{"id":"0b3c8c43-6a2d-4c55-9d4c-3d6c1f6a0a01","name":"M","amount":30000,"available":false}]}
            """));
        ApiCatalogStore store = Store(catalog);

        CatalogEntry entry = new()
        {
            Name = " Ankara wrap dress ",
            Kind = CatalogKind.Product,
            Group = "Dresses",
            Photos = ["data:image/jpeg;base64," + Convert.ToBase64String([1, 2, 3])],
            Options = [new ItemOption { Name = PriceWords.VariantList, Values = ["M"] }],
            Variants = [new ItemVariant { Key = "M", Price = 30000, IsAvailable = false }],
            AvailabilityIsUnconfirmed = true,
            EditorDraft = new CatalogEditorDraft { Pricing = "variants", Variants = [new CatalogVariantDraft("M", "30k", true)] },
        };
        CatalogEntry saved = await store.CreateAsync(entry);

        Assert.Equal(["POST api/catalog/items/photos", "GET api/catalog/categories", "POST api/catalog/categories", "POST api/catalog/items"], catalog.Calls);
        JsonElement sent = catalog.Bodies["POST api/catalog/items"];
        Assert.Equal("Ankara wrap dress", sent.GetProperty("name").GetString());
        Assert.Equal("product", sent.GetProperty("kind").GetString());
        Assert.Equal("draft", sent.GetProperty("status").GetString());
        Assert.Equal("tenants/t/items/a.jpg", sent.GetProperty("photos")[0].GetString());
        Assert.Equal("5a0c6f1e-6d2b-4d8e-9a3c-1b2c3d4e5f60", sent.GetProperty("category_id").GetString());
        Assert.False(sent.TryGetProperty("availability", out _));
        JsonElement variant = sent.GetProperty("pricing").GetProperty("variants")[0];
        Assert.False(variant.GetProperty("available").GetBoolean());
        Assert.Equal("30k", sent.GetProperty("form_draft").GetProperty("variants")[0].GetProperty("price").GetString());

        Assert.Equal(ItemId, saved.Id);
        Assert.False(saved.Variants[0].IsAvailable);
    }

    [Fact]
    public async Task An_item_read_back_saves_with_its_variant_ids_and_photo_keys()
    {
        FakeCatalog catalog = new();
        catalog.Respond("GET api/catalog/items?page=1&page_size=100", $$"""
            {"items":[{{Item("""
                "kind":"service","status":"published","hidden":true,"duration":"5 hours",
                "photos":[{"key":"tenants/t/items/b.png","url":"http://photos/b.png"}],
                "pricing":{"mode":"variants","per":"head","currency":"NGN","variants":[{"id":"0b3c8c43-6a2d-4c55-9d4c-3d6c1f6a0a01","name":"Short","amount":20000,"available":true}]},
                "availability":{"mode":"bookings","available":false}
                """)}}],"total":1,"page":1,"page_size":100}
            """);
        catalog.Respond($"PUT api/catalog/items/{ItemId}", Item("""
            "status":"published","pricing":{"mode":"single","currency":"NGN","amount":1000,"variants":[]}
            """));
        ApiCatalogStore store = Store(catalog);

        CatalogEntry item = Assert.Single(await store.ListAsync());
        Assert.Equal(CatalogKind.Service, item.Kind);
        Assert.Equal(CatalogStatus.Live, item.Status);
        Assert.True(item.IsHidden);
        Assert.True(item.IsAvailable);
        Assert.Equal("5 hours", item.Duration);
        Assert.Equal("per head", item.PriceUnit);
        Assert.Equal(["http://photos/b.png"], item.Photos);

        item.Name = "Knotless braids";
        await store.SaveAsync(item);

        Assert.DoesNotContain("POST api/catalog/items/photos", catalog.Calls);
        JsonElement sent = catalog.Bodies[$"PUT api/catalog/items/{ItemId}"];
        Assert.Equal("tenants/t/items/b.png", sent.GetProperty("photos")[0].GetString());
        Assert.Equal(Variant, sent.GetProperty("pricing").GetProperty("variants")[0].GetProperty("id").GetString());
        Assert.Equal("head", sent.GetProperty("pricing").GetProperty("per").GetString());
        Assert.Equal("bookings", sent.GetProperty("availability").GetProperty("mode").GetString());
        Assert.Equal("5 hours", sent.GetProperty("duration").GetString());
        Assert.True(sent.GetProperty("hidden").GetBoolean());
    }

    [Fact]
    public async Task What_the_ai_made_of_each_photo_comes_through_for_the_item_page()
    {
        FakeCatalog catalog = new();
        catalog.Respond("GET api/catalog/items?page=1&page_size=100", $$"""
            {"items":[{{Item("""
                "status":"published","hidden":false,
                "photos":[{"key":"tenants/t/items/a.png","url":"http://photos/a.png"},{"key":"tenants/t/items/b.png","url":"http://photos/b.png"}],
                "pricing":{"mode":"single","currency":"NGN","amount":5000,"variants":[]},
                "ai":{"state":"needs_attention","sent_at":"2026-11-11T09:00:00Z","reported_at":"2026-11-11T09:00:42Z","searchable":true,
                      "text_status":"embedded","text_reason":null,
                      "photos":[
                        {"media_id":"7a8b9c0d-1e2f-4a3b-8c4d-5e6f7a8b9c0d","photo_key":"tenants/t/items/a.png","status":"embedded","caption":"A blue scarf.","reason_code":null,"reason":null},
                        {"media_id":"9c0d1e2f-3a4b-4c5d-8e6f-7a8b9c0d1e2f","photo_key":"tenants/t/items/b.png","status":"rejected","caption":"A cartoon chicken.","reason_code":"client_err","reason":"The photo shows a cartoon chicken."}]}
                """)}}],"total":1,"page":1,"page_size":100}
            """);
        ApiCatalogStore store = Store(catalog);

        CatalogEntry item = Assert.Single(await store.ListAsync());

        Assert.Equal(AiProgress.NeedsAttention, item.Ai);
        Assert.NotNull(item.AiReportedAt);
        Assert.Collection(item.AiPhotos,
            learned =>
            {
                Assert.True(learned.Learned);
                Assert.Equal("http://photos/a.png", learned.PhotoUrl);
                Assert.Equal("A blue scarf.", learned.Caption);
            },
            rejected =>
            {
                Assert.True(rejected.Rejected);
                Assert.Equal("http://photos/b.png", rejected.PhotoUrl);
                Assert.Equal("The photo shows a cartoon chicken.", rejected.Reason);
            });
    }

    [Fact]
    public async Task The_services_reason_comes_through_when_it_refuses_a_save()
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items", """{"error_code":"VALIDATION","message":"Each variant needs an amount to make the item live."}""", HttpStatusCode.BadRequest);
        ApiCatalogStore store = Store(catalog);

        var error = await Assert.ThrowsAsync<global::Parrot.Web.Services.ApiException>(() =>
            store.CreateAsync(new CatalogEntry { Name = "Dress", Status = CatalogStatus.Live, Price = 1000 }));
        Assert.Equal("Each variant needs an amount to make the item live.", error.UserMessage);
    }

    // A photo never leaves a save quietly: refused, or one the catalog can't be given, the save stops and says
    // which photo and why (designer, 23 November 2026).
    [Fact]
    public async Task A_refused_photo_stops_the_save_with_the_catalogs_reason_and_which_photo()
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items/photos", """{"error_code":"VALIDATION","message":"A photo must be a JPEG, PNG or WebP image."}""", HttpStatusCode.BadRequest);
        string photo = "data:image/gif;base64," + Convert.ToBase64String([1, 2, 3]);

        var error = await Assert.ThrowsAsync<CatalogPhotoException>(() =>
            Store(catalog).CreateAsync(new CatalogEntry { Name = "Dress", Price = 1000, Photos = [photo] }));

        Assert.Equal((photo, 1, "A photo must be a JPEG, PNG or WebP image."), (error.Photo, error.Number, error.Reason));
        Assert.DoesNotContain("POST api/catalog/items", catalog.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Photos can be up to 5 MB. Remove it, or choose a smaller one.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Photos can't be saved right now. Remove it to save the item without it, or try again later.")]
    public async Task A_bare_refusal_gets_plain_words(HttpStatusCode status, string reason)
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items/photos", "", status);

        var error = await Assert.ThrowsAsync<CatalogPhotoException>(() => Store(catalog).CreateAsync(
            new CatalogEntry { Name = "Dress", Price = 1000, Photos = ["data:image/jpeg;base64," + Convert.ToBase64String([1])] }));

        Assert.Equal(reason, error.Reason);
    }

    [Fact]
    public async Task A_photo_the_catalog_cant_be_given_stops_the_save_instead_of_being_dropped()
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items/photos", """{"key":"tenants/t/items/a.jpg","url":"http://photos/a.jpg"}""");
        const string fromPost = "https://scontent.cdninstagram.com/v/post.jpg";

        var error = await Assert.ThrowsAsync<CatalogPhotoException>(() => Store(catalog).CreateAsync(new CatalogEntry
        {
            Name = "Dress", Price = 1000, Photos = ["data:image/jpeg;base64," + Convert.ToBase64String([1]), fromPost],
        }));

        Assert.Equal((fromPost, 2), (error.Photo, error.Number));
        Assert.DoesNotContain("POST api/catalog/items", catalog.Calls);
    }

    [Fact]
    public async Task A_server_failure_on_a_photo_is_not_blamed_on_the_photo()
    {
        FakeCatalog catalog = new();
        catalog.Respond("POST api/catalog/items/photos", "", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<global::Parrot.Web.Services.ApiException>(() => Store(catalog).CreateAsync(
            new CatalogEntry { Name = "Dress", Price = 1000, Photos = ["data:image/jpeg;base64," + Convert.ToBase64String([1])] }));

        Assert.IsNotType<CatalogPhotoException>(error);
    }

    private static string Item(string fields) =>
        $$"""{"id":"{{ItemId}}","name":"Ankara wrap dress","kind":"product","created_at":"2026-11-02T10:00:00Z","updated_at":"2026-11-02T10:00:00Z",{{fields}}}""";

    private static ApiCatalogStore Store(FakeCatalog catalog)
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "token", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        return new ApiCatalogStore(new HttpClient(catalog) { BaseAddress = new Uri("http://catalog/") }, auth);
    }

    private sealed class FakeCatalog : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = [];

        public List<string> Calls { get; } = [];

        public Dictionary<string, JsonElement> Bodies { get; } = [];

        public void Respond(string call, string body, HttpStatusCode status = HttpStatusCode.OK) => _responses[call] = (body, status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer token", request.Headers.Authorization?.ToString());
            string call = $"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";
            Calls.Add(call);
            if (request.Content is { Headers.ContentType.MediaType: "application/json" } content)
                Bodies[call] = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            (string body, HttpStatusCode status) = _responses.TryGetValue(call, out var found) ? found : ("{}", HttpStatusCode.NotFound);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
