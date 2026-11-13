using Parrot.Application.Auth;

namespace Parrot.Api.Mcp;

/// <summary>
/// The agent's MCP server: the tools the AI agent can call while it talks to a business's
/// customers. Mounted at <see cref="Path"/>, open only to a service token with
/// <c>agent:service</c> scope (the one the orchestrator mints per workspace).
/// </summary>
public static class McpEndpoint
{
    public const string Path = "/api/mcp";

    /// <summary>The MCP server and its tools. The caller registers <see cref="Parrot.Application.Agents.ICatalogToolsClient"/>.</summary>
    public static IServiceCollection AddAgentTools(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer(options => options.ServerInfo = new () { Name = "parrot", Title = "Parrot", Version = "1" })
            .WithHttpTransport()
            .WithTools<ProductTools>();
        return services;
    }

    /// <summary>Mounts the server behind the service-token policy.</summary>
    public static IEndpointConventionBuilder MapAgentTools(this IEndpointRouteBuilder endpoints)
        => endpoints.MapMcp(Path).RequireAuthorization(ServiceScopes.PolicyName);
}
