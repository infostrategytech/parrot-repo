using System.Text.Json;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>Turns an item's <see cref="ItemAiStatus"/> row into what the business sees.</summary>
public static class ItemAiView
{
    /// <summary>
    /// Null when nothing has been sent for the item yet. Otherwise "updating" until the AI
    /// reports on the latest save. Then, following the AI team's reason codes: client_err
    /// (a rejected photo, or text the AI couldn't use) is the business's to fix; server_err
    /// is the AI's side. A draft, hidden or removed item is "not listed". A status the
    /// contract doesn't name yet counts as needing attention, as the contract asks.
    /// </summary>
    public static ItemAi? Of(ItemAiStatus? row, IReadOnlyDictionary<Guid, string> photoKeys)
    {
        if (row?.SentEventId is null)
        {
            return null;
        }

        if (row.ReportedEventId != row.SentEventId)
        {
            return new ItemAi(ItemAiState.Updating, row.SentAt, row.ReportedAt, null, null, null, []);
        }

        ItemAiState state = row.Status?.ToLowerInvariant() switch
        {
            "removed" or "unlisted" => ItemAiState.NotListed,
            "failed" => Is(row.TextReasonCode, "client_err") ? ItemAiState.NeedsAttention : ItemAiState.Failed,
            "indexed" when row.PhotosRejected > 0 => ItemAiState.NeedsAttention,
            "indexed" when row.PhotosFailed > 0 || Is(row.PhotosStatus, "failed") => ItemAiState.Failed,
            "indexed" => ItemAiState.Ready,
            _ => ItemAiState.NeedsAttention,
        };
        return new ItemAi(state, row.SentAt, row.ReportedAt, row.Searchable, row.TextStatus, row.TextReason, Photos(row.Photos, photoKeys));
    }

    private static List<ItemAiPhoto> Photos(string? photosJson, IReadOnlyDictionary<Guid, string> photoKeys)
    {
        List<ItemAiPhoto> photos = [];
        if (string.IsNullOrEmpty(photosJson))
        {
            return photos;
        }

        using JsonDocument doc = JsonDocument.Parse(photosJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return photos;
        }

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            if (Text(item, "status") is not { } status)
            {
                continue;
            }

            Guid mediaId = Guid.TryParse(Text(item, "media_id"), out Guid id) ? id : Guid.Empty;
            photos.Add(new ItemAiPhoto(
                mediaId,
                photoKeys.GetValueOrDefault(mediaId),
                status.ToLowerInvariant(),
                Text(item, "caption"),
                Text(item, "reason_code"),
                Text(item, "reason")));
        }

        return photos;
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
