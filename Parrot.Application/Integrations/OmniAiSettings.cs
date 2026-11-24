namespace Parrot.Application.Integrations;

/// <summary>
/// Where the AI orchestrator (Omni-AI) is, and whether this deployment talks
/// to it at all. Off, agents are saved as drafts and no call leaves for it.
/// </summary>
public class OmniAiSettings
{
    public const string SectionName = "OmniAi";

    public string BaseUrl { get; set; } = "http://localhost:8000";

    public bool Enabled { get; set; }

    /// <summary>
    /// The orientation service ("Tell me about your business", by voice through ElevenLabs).
    /// Empty: it is not offered. It is a separate service from BaseUrl's for now.
    /// </summary>
    public string? OrientationUrl { get; set; }

    /// <summary>How long one call may take. An approval decision resumes a whole turn, so this is longer than a webhook's budget.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Several agents per channel, answered through the AI service's super agent. Turn on together with the
    /// service's own switch (OMNI_MULTI_AGENT_ENABLED); off, Parrot works as it did with one agent per channel.
    /// </summary>
    public bool MultiAgentEnabled { get; set; }
}
