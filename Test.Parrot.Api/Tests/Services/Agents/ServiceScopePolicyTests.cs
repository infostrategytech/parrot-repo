using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Api.Security;
using Parrot.Application.Auth;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The policy as Program.cs registers it, evaluated the way [Authorize(Policy = ...)] does.
public class ServiceScopePolicyTests
{
    private static async Task<bool> AllowedAsync(ClaimsPrincipal user)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddServiceScopePolicy();
        await using ServiceProvider provider = services.BuildServiceProvider();

        AuthorizationResult result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, ServiceScopes.PolicyName);
        return result.Succeeded;
    }

    private static ClaimsPrincipal Bearer(params Claim[] claims) => new(new ClaimsIdentity(claims, "Bearer"));

    [Fact]
    public async Task ServiceScopePolicy_WithTheAgentServiceScope_Allows()
    {
        ClaimsPrincipal service = Bearer(
            new Claim(ClaimTypes.NameIdentifier, "owner-1"),
            new Claim("tenant_id", "owner-1"),
            new Claim("scope", "agent:service catalog:ai-tools"),
            new Claim("svc", "omni-ai"));

        Assert.True(await AllowedAsync(service));
    }

    // A session token carries the catalog owner scopes and never agent:service.
    [Fact]
    public async Task ServiceScopePolicy_WithANormalUserToken_Denies()
    {
        ClaimsPrincipal user = Bearer(
            new Claim(ClaimTypes.NameIdentifier, "owner-1"),
            new Claim(ClaimTypes.Email, "owner@example.com"),
            new Claim("tenant_id", "owner-1"),
            new Claim("scope", "catalog:read catalog:write catalog:publish catalog:admin"));

        Assert.False(await AllowedAsync(user));
    }

    [Fact]
    public async Task ServiceScopePolicy_WithNoScopeClaim_Denies()
    {
        Assert.False(await AllowedAsync(Bearer(new Claim(ClaimTypes.NameIdentifier, "owner-1"))));
    }

    [Fact]
    public async Task ServiceScopePolicy_Unauthenticated_Denies()
    {
        Assert.False(await AllowedAsync(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task ServiceScopePolicy_ReadsTheScpClaimToo()
    {
        Assert.True(await AllowedAsync(Bearer(new Claim(ClaimTypes.NameIdentifier, "owner-1"), new Claim("scp", "agent:service"))));
    }

    // "agent:services" or "agent:service2" is not the scope.
    [Fact]
    public async Task ServiceScopePolicy_MatchesTheWholeScopeOnly()
    {
        Assert.False(await AllowedAsync(Bearer(new Claim(ClaimTypes.NameIdentifier, "owner-1"), new Claim("scope", "agent:services"))));
    }
}
