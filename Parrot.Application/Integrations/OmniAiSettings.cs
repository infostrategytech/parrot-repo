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

    /// <summary>How long one call may take. An approval decision resumes a whole turn, so this is longer than a webhook's budget.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
