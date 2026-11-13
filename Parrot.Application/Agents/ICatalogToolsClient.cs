using System.Text.Json;

namespace Parrot.Application.Agents;

/// <summary>
/// Calls the catalog's AI tools (<c>/api/catalog/ai/tools/{tool}</c>) for one workspace,
/// so the agent's tool calls can be answered from the live catalog: published products,
/// their prices and stock. The workspace is the caller's to supply from a trusted
/// source (the agent's token), never from the agent's arguments.
/// </summary>
public interface ICatalogToolsClient
{
    /// <summary>The tool's answer as the catalog sent it: an envelope with the tool name, data_as_of, a notice and the result.</summary>
    /// <exception cref="CatalogToolException">The catalog refused the call or could not be reached.</exception>
    Task<JsonElement> CallAsync(string workspaceId, string tool, object arguments, CancellationToken ct);
}

/// <summary>Why a catalog tool call failed, in words the agent can act on.</summary>
public sealed class CatalogToolException(string message, bool refused) : Exception(message)
{
    /// <summary>True when the catalog refused the arguments (the agent can fix them); false when it could not be reached.</summary>
    public bool Refused { get; } = refused;
}
