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
}
