using System.Text.Json;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>Turns an item's <see cref="ItemAiStatus"/> row into what the business sees.</summary>
public static class ItemAiView
{
    /// <summary>
    /// Null when nothing has been sent for the item yet. Otherwise "updating" until the AI
    /// reports on the latest save; then a rejected photo needs the business's attention,
    /// a failure is the AI team's to retry, and anything else is ready.
    /// </summary>
    public static ItemAi? Of(ItemAiStatus? row, IReadOnlyDictionary<Guid, string> photoKeys)
    {
        if (row?.SentEventId is null)
        {
            return null;
        }

        if (row.ReportedEventId != row.SentEventId)
        {
            return new ItemAi(ItemAiState.Updating, row.SentAt, row.ReportedAt, []);
        }

        List<ItemAiIssue> issues = Issues(row.Photos, photoKeys);
        bool failed = row.PhotosFailed > 0 || Is(row.Status, "failed") || Is(row.TextStatus, "failed");
        ItemAiState state = row.PhotosRejected > 0 ? ItemAiState.NeedsAttention
            : failed ? ItemAiState.Failed
            : ItemAiState.Ready;
        return new ItemAi(state, row.SentAt, row.ReportedAt, issues);
    }

    private static List<ItemAiIssue> Issues(string? photosJson, IReadOnlyDictionary<Guid, string> photoKeys)
    {
        List<ItemAiIssue> issues = [];
        if (string.IsNullOrEmpty(photosJson))
        {
            return issues;
        }

        using JsonDocument doc = JsonDocument.Parse(photosJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return issues;
        }

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            string? status = Text(item, "status");
            if (!Is(status, "rejected") && !Is(status, "failed"))
            {
                continue;
            }

            Guid mediaId = Guid.TryParse(Text(item, "media_id"), out Guid id) ? id : Guid.Empty;
            issues.Add(new ItemAiIssue(mediaId, photoKeys.GetValueOrDefault(mediaId), status!.ToLowerInvariant(), Text(item, "reason")));
        }

        return issues;
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
