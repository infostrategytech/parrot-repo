using System.ComponentModel;

namespace Parrot.Application.DTOs.Auth;

/// <summary>What a successful sign-in, sign-up or verification hands back.</summary>
[Description("A session, plus the profile as it stands at the moment it was issued.")]
public record AuthResponse(
    [property: Description("The session token. Send it as Authorization: Bearer <token>.")]
    string Token,
    [property: Description("When the token stops being accepted.")]
    DateTime ExpiresAt,
    [property: Description("The account's identifier.")]
    string UserId,
    [property: Description("The address on the account.")]
    string Email,
    [property: Description("The active workspace's business name, or null before setup has been completed.")]
    string? BusinessName,
    [property: Description("The active workspace's logo URL, or null when none has been set.")]
    string? LogoUrl,
    [property: Description("The phone number in international display form, e.g. +2348012345678, or null when none is on the account.")]
    string? PhoneNumber,
    [property: Description("The workspace this session works in. Everything the token reads and writes belongs to it.")]
    string WorkspaceId,
    [property: Description("\"Owner\" in a workspace the person made; otherwise the role they were given, e.g. \"Analyst\".")]
    string Role,
    [property: Description("What this session may do in the workspace, e.g. conversations.reply. An owner has every permission, team.manage and workspace.manage among them.")]
    IReadOnlyList<string> Permissions
);
