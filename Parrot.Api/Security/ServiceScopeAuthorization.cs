using Microsoft.AspNetCore.Authorization;
using Parrot.Application.Auth;

namespace Parrot.Api.Security;

/// <summary>
/// The one policy a service token can satisfy and a session token cannot: it
/// needs the <c>agent:service</c> scope, which sign-in never grants. Nothing
/// else changes: every other endpoint keeps its plain [Authorize], and an
/// endpoint under this policy is closed to everyone without the scope.
/// </summary>
public static class ServiceScopeAuthorization
{
    public static IServiceCollection AddServiceScopePolicy(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ServiceScopes.PolicyName, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => ServiceScopes.HasScope(context.User, ServiceScopes.AgentService)));
        return services;
    }
}
