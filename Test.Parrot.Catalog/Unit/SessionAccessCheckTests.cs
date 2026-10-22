using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Security;

namespace Test.Parrot.Catalog.Unit;

// A Parrot session token's catalog scopes are only as current as the token, so each
// request is checked against the member's access in Parrot now.
public class SessionAccessCheckTests
{
    private const string Workspace = "5d2e9b7c-0000-0000-0000-000000000001";

    private sealed class Api(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public string? SentToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://parrot-api/") };
    }

    private static SessionAccessCheck Check(Api api) =>
        new(new Factory(api), new MemoryCache(new MemoryCacheOptions()), new CatalogAuthOptions { AccessCheckUrl = "http://parrot-api/" },
            NullLogger<SessionAccessCheck>.Instance);

    private static ClaimsPrincipal Session(string scopes = "catalog:read catalog:write catalog:publish catalog:admin") =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", "ada"),
            new Claim(SessionAccessCheck.WorkspaceClaim, Workspace),
            new Claim(CatalogClaims.TenantId, Workspace),
            new Claim(CatalogClaims.Scope, scopes),
        ], "Bearer"));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheTokensScopes_AreReplacedWithTheMembersCurrentOnes()
    {
        Api api = new(_ => Json($$"""{"workspaceId":"{{Workspace}}","catalogScopes":"catalog:read"}"""));
        ClaimsPrincipal principal = Session();

        SessionAccessCheck.Verdict verdict = await Check(api).CheckAsync(principal, "raw-token", default);
        SessionAccessCheck.ApplyScopes(principal, verdict.CatalogScopes!);

        Assert.Null(verdict.RefusedBecause);
        Assert.Equal("raw-token", api.SentToken);
        Assert.Equal(["catalog:read"], CatalogAuthentication.ScopesOf(principal));
    }

    [Fact]
    public async Task AMemberWithNoCatalogPermissions_IsLeftWithNoScopes()
    {
        Api api = new(_ => Json($$"""{"workspaceId":"{{Workspace}}","catalogScopes":""}"""));
        ClaimsPrincipal principal = Session();

        SessionAccessCheck.ApplyScopes(principal, (await Check(api).CheckAsync(principal, "t", default)).CatalogScopes!);

        Assert.Empty(CatalogAuthentication.ScopesOf(principal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ARemovedMember_IsRefused(HttpStatusCode status)
    {
        Api api = new(_ => Json("""{"message":"You no longer have access to this workspace."}""", status));

        Assert.NotNull((await Check(api).CheckAsync(Session(), "t", default)).RefusedBecause);
    }

    // A stale token must not win because Parrot cannot be reached.
    [Fact]
    public async Task WhenParrotCannotBeReached_TheRequestIsRefused()
    {
        Api api = new(_ => throw new HttpRequestException("connection refused"));

        Assert.NotNull((await Check(api).CheckAsync(Session(), "t", default)).RefusedBecause);
    }

    [Fact]
    public async Task AnAnswerAboutAnotherWorkspace_IsRefused()
    {
        Api api = new(_ => Json("""{"workspaceId":"another","catalogScopes":"catalog:admin"}"""));

        Assert.NotNull((await Check(api).CheckAsync(Session(), "t", default)).RefusedBecause);
    }

    [Fact]
    public void OnlySessionTokens_AreChecked()
    {
        ClaimsPrincipal service = new(new ClaimsIdentity([new Claim(CatalogClaims.TenantId, Workspace), new Claim(CatalogClaims.Scope, "catalog:ai-tools")], "Bearer"));

        Assert.False(SessionAccessCheck.Applies(service));
        Assert.True(SessionAccessCheck.Applies(Session()));
    }
}
