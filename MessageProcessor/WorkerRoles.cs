namespace MessageProcessor;

/// <summary>
/// Which jobs this process works on. The message processor, the media worker and the send worker are one program
/// started three ways, so each can run as its own service and be scaled on its own backlog: a slow download or a
/// slow channel then never takes capacity from storing incoming messages. Left unset, one process does all three.
/// </summary>
[Flags]
public enum WorkerRoles
{
    None = 0,
    Messages = 1,
    Media = 2,
    Outbound = 4,
    AiReply = 8,
    All = Messages | Media | Outbound | AiReply,
}

public static class WorkerRoleSettings
{
    public const string Key = "Worker:Roles";

    /// <summary>"messages", "media", "outbound", "aireplies" or "all", comma separated. Unset or empty is all. Anything else stops startup.</summary>
    public static WorkerRoles Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return WorkerRoles.All;

        WorkerRoles roles = WorkerRoles.None;
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            roles |= part.ToLowerInvariant() switch
            {
                "all" => WorkerRoles.All,
                "messages" => WorkerRoles.Messages,
                "media" => WorkerRoles.Media,
                "outbound" => WorkerRoles.Outbound,
                "aireplies" => WorkerRoles.AiReply,
                _ => throw new InvalidOperationException($"{Key} has '{part}'; use messages, media, outbound, aireplies or all."),
            };
        }

        return roles == WorkerRoles.None ? WorkerRoles.All : roles;
    }
}
