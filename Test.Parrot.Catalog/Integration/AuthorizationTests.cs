using System.Net;
using System.Net.Http.Headers;
using Parrot.Catalog.Application.Security;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

[Collection(CatalogApiCollection.Name)]
public class AuthorizationTests
{
    private readonly CatalogApiFixture _fx;

    public AuthorizationTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task Anonymous_and_bad_tokens_are_401()
    {
        using HttpClient anonymous = _fx.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/catalog/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);

        using HttpClient expired = WithToken(CatalogApiFixture.Token(_fx.TenantA, [CatalogScopes.Read], expires: DateTime.UtcNow.AddHours(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/catalog/products")).StatusCode);

        using HttpClient wrongIssuer = WithToken(CatalogApiFixture.Token(_fx.TenantA, [CatalogScopes.Read], issuer: "someone-else"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongIssuer.GetAsync("/api/catalog/products")).StatusCode);

        using HttpClient wrongKey = WithToken(CatalogApiFixture.Token(_fx.TenantA, [CatalogScopes.Read], secret: "another-secret-that-is-also-long-enough-0123456789"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.GetAsync("/api/catalog/products")).StatusCode);
    }

    [PostgresFact]
    public async Task Missing_tenant_or_scope_is_403_deny_by_default()
    {
        using HttpClient noTenant = WithToken(CatalogApiFixture.Token(null, [CatalogScopes.Admin, CatalogScopes.Read]));
        Assert.Equal(HttpStatusCode.Forbidden, (await noTenant.GetAsync("/api/catalog/products")).StatusCode);

        using HttpClient noScope = WithToken(CatalogApiFixture.Token(_fx.TenantA, []));
        Assert.Equal(HttpStatusCode.Forbidden, (await noScope.GetAsync("/api/catalog/products")).StatusCode);

        using HttpClient unknownScope = WithToken(CatalogApiFixture.Token(_fx.TenantA, ["catalog:superuser"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await unknownScope.GetAsync("/api/catalog/products")).StatusCode);
    }

    [PostgresFact]
    public async Task Scopes_gate_each_surface()
    {
        Guid laptop = _fx.SeedA!.ProductIds["laptop"];

        using HttpClient read = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/catalog/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id = laptop }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/catalog/admin/products/{laptop}/publish", null)).StatusCode);

        using HttpClient write = _fx.Client(_fx.TenantA, CatalogScopes.Write);
        Assert.Equal(HttpStatusCode.OK, (await write.GetAsync("/api/catalog/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.PostAsync($"/api/catalog/admin/products/{laptop}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.PostAsync("/api/catalog/admin/types", CatalogApiFixture.Raw("""{"key":"t","name":"T","kind":"good"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.GetAsync("/api/catalog/products")).StatusCode);

        using HttpClient ai = _fx.Client(_fx.TenantA, CatalogScopes.AiTools);
        Assert.Equal(HttpStatusCode.OK, (await ai.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id = laptop }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ai.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ai.GetAsync("/api/catalog/admin/products")).StatusCode);

        using HttpClient admin = _fx.Client(_fx.TenantA, CatalogScopes.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/catalog/admin/types")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id = laptop }))).StatusCode);
    }

    [PostgresFact]
    public async Task Oversized_bodies_are_refused()
    {
        using HttpClient write = _fx.Client(_fx.TenantA, CatalogScopes.Write);
        string huge = "{\"name\":\"" + new string('a', 1_100_000) + "\"}";
        HttpResponseMessage response = await write.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw(huge));
        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, response.StatusCode.ToString());
    }

    private HttpClient WithToken(string token)
    {
        HttpClient client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
