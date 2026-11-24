using System.Text.Json;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// Reads the agents the AI service's super agent asked for a reply (ai.reply.requested's contributors), as display
/// data: an entry without an agent id or name is left out, and a list it can't read gives none, so the reply itself
/// always goes. Three at most, each one's words cut to 1,500 characters, as the AI service caps them.
/// </summary>
public static class ReplyContributors
{
    public const int MaxContributors = 3;
    public const int MaxWords = 1500;

    private static readonly HashSet<string> Statuses = ["answered", "failed", "timed_out"];

    public static List<ReplyContributor> Read(JsonElement? contributors)
    {
        if (contributors is not { ValueKind: JsonValueKind.Array } list) return [];

        List<ReplyContributor> read = [];
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (read.Count == MaxContributors) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (Text(entry, "agentId") is not { Length: > 0 } agentId || Text(entry, "name") is not { Length: > 0 } name) continue;

            string? status = Text(entry, "status");
            read.Add(new ReplyContributor
            {
                AgentId = agentId,
                Name = name,
                Avatar = Text(entry, "avatar"),
                Question = Cut(Text(entry, "input")),
                Answer = Cut(Text(entry, "output")),
                // Anything else is not an answer the reply was made from.
                Status = status is not null && Statuses.Contains(status) ? status : "failed",
            });
        }

        return read;
    }

    private static string? Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Cut(string? words) => words is null ? "" : words.Length <= MaxWords ? words : words[..MaxWords];
}
