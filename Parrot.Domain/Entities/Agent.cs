namespace Parrot.Domain.Entities;

public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The workspace the agent works for.</summary>
    public required string WorkspaceId { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public required string MascotUrl { get; set; }

    public bool SupportsHumanTakeover { get; set; } = true;

    /// <summary>Turned on: it answers customers on its channels. Off, it answers no one.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The channel keys it answers on ("whatsapp", "facebook", "instagram"),
    /// lower case. Empty: nowhere yet, whatever <see cref="IsActive"/> says.
    /// </summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>
    /// The owner's choices on the AI Agents screens (its job, how it talks,
    /// what it may do, when it hands over), as JSON in the AgentSettings
    /// shape. Kept as one document rather than columns: the orchestrator reads
    /// it whole and the screens change it as one thing.
    /// </summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>Saved but never turned on: it answers no one. Turning it on ends the draft.</summary>
    public bool IsDraft { get; set; }

    /// <summary>Bumped on every save, so a consumer can tell a stale configuration from a fresh one.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Set when the owner deletes it. The row stays, so the activity that names the agent still reads.</summary>
    public DateTime? DeletedAt { get; set; }
}
