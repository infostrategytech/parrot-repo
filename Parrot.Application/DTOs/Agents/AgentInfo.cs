namespace Parrot.Application.DTOs.Agents;

/// <summary>What the owner saves about an agent: the body of a create and of an update.</summary>
public class AgentInfo
{
    required public string Name { get; set; }

    required public string Description { get; set; }

    required public string MascotUrl { get; set; }

    required public bool SupportsHumanTakeover { get; set; } = true;

    /// <summary>The channel keys it answers on, e.g. "whatsapp". Null or empty: nowhere yet.</summary>
    public List<string>? Channels { get; set; }

    /// <summary>Its job, voice, permissions and handover rules. Null: the defaults.</summary>
    public AgentSettingsDto? Settings { get; set; }

    /// <summary>
    /// On create: save it without turning it on. Ignored on update, where
    /// the draft ends only through the live switch, so saving settings never
    /// turns an agent on or off by accident.
    /// </summary>
    public bool IsDraft { get; set; }
}
