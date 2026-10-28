namespace Parrot.Application.DTOs.Integrations;

/// <summary>One of the workspace's connections: its id and its channel key ("whatsapp", "facebook", "instagram", "tiktok").</summary>
public sealed record WorkspaceConnectionResponse(Guid Id, string Channel);
