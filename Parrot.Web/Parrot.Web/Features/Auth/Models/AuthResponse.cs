using System.Text.Json.Serialization;

namespace Parrot.Web.Features.Auth.Models;

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // The workspace this session works in. Everything the token reads and
    // writes belongs to it; switching workspaces issues a new session.
    public string WorkspaceId { get; set; } = string.Empty;

    // A session saved before workspaces has no id. The user's own id is their
    // first workspace, which is what the API assumes for such a token too.
    [JsonIgnore]
    public string CurrentWorkspaceId => string.IsNullOrEmpty(WorkspaceId) ? UserId : WorkspaceId;

    // The current workspace's name. Null until the welcome card has saved one.
    public string? BusinessName { get; set; }

    // The current workspace's logo, or null when none has been uploaded.
    public string? LogoUrl { get; set; }

    // "Owner", or the role the person was given in this workspace.
    public string Role { get; set; } = "Owner";

    // What the session may do here, e.g. "conversations.reply". The API checks
    // every request itself; this is for showing only what can be used.
    public List<string> Permissions { get; set; } = [];

    // A session saved before teams carries no role, and was always its owner's.
    [JsonIgnore]
    public bool IsOwner => string.IsNullOrEmpty(Role) || Role == "Owner";

    public bool Can(string permission) => IsOwner || Permissions.Contains(permission);
}
