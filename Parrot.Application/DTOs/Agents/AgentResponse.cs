namespace Parrot.Application.DTOs.Agents;

public class AgentResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MascotUrl { get; set; } = string.Empty;
    public bool SupportsHumanTakeover { get; set; }
    public bool IsActive { get; set; }

    /// <summary>The channel keys it answers on, lower case.</summary>
    public List<string> Channels { get; set; } = [];

    public AgentSettingsDto Settings { get; set; } = new();

    /// <summary>Saved but never turned on.</summary>
    public bool IsDraft { get; set; }

    /// <summary>Bumped on every save.</summary>
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
