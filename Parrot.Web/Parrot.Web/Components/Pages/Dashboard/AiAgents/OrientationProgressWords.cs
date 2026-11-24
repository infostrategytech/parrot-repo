using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

/// <summary>
/// What the orientation progress bar says (owner, 23 November 2026), so teaching an agent has a
/// visible end: how far along it is, what happens next (the owner's turn first), and each topic.
/// </summary>
public static class OrientationProgressWords
{
    /// <summary>The words above the bar.</summary>
    public static string Label(OrientationProgress progress, string name) => progress.Ready
        ? $"Done. {name} knows enough to start."
        : $"{progress.ReviewedCount} of {progress.RequiredCount} topics done · {progress.Percent}%";

    /// <summary>
    /// The line under the bar, or none. <paramref name="formShown"/>: a form waits where the box is;
    /// <paramref name="inCall"/>: a voice chat is on, which keeps forms out of sight until it ends.
    /// </summary>
    public static string? NextStep(OrientationProgress progress, bool formShown, bool inCall)
    {
        if (progress.Ready) return null;
        if (progress.PendingReview is { Count: > 0 } waiting)
            return formShown ? $"Your turn: approve the form below to finish {TopicNames(progress, waiting)}."
                : inCall ? "A form is waiting for you. End the voice chat to see it."
                : $"Your form for {TopicNames(progress, waiting)} is on its way.";
        if (progress.GeneratingReview is { Count: > 0 } preparing)
            return $"Preparing a form for you to check {TopicNames(progress, preparing)}…";
        if (progress.ActiveTopic is { } now)
            return now.DetailsTotal > 0 ? $"Now: {now.Label}, {Details(now)}" : $"Now: {now.Label}";
        return null;
    }

    /// <summary>Why it can't be turned on yet: how much is left to teach it.</summary>
    public static string NotReady(OrientationProgress progress, string name)
    {
        int left = progress.RequiredCount - progress.ReviewedCount;
        return left switch
        {
            <= 0 => $"Finish teaching {name} first.",
            1 => $"Finish teaching {name} first: 1 topic left.",
            _ => $"Finish teaching {name} first: {left} topics left.",
        };
    }

    /// <summary>A topic's state in the list: done, yours (a form to approve), preparing, now or later.</summary>
    public static string StateOf(string? status) => status switch
    {
        "reviewed" => "done",
        "pending_review" => "yours",
        "generating_review" => "preparing",
        "in_progress" => "now",
        _ => "later",
    };

    /// <summary>A topic's state in words, beside its name.</summary>
    public static string Status(OrientationTopic topic)
    {
        string status = StateOf(topic.Status) switch
        {
            "done" => "Done",
            "yours" => "Waiting for you to approve",
            "preparing" => "Preparing your form",
            "now" when topic.DetailsTotal > 0 => $"In progress, {Details(topic)}",
            "now" => "In progress",
            _ => "Not started",
        };
        return topic.Required ? status : $"{status} · optional";
    }

    // The topics' own names where the snapshot has them; otherwise the key, readable.
    private static string TopicNames(OrientationProgress progress, IReadOnlyList<string> keys)
    {
        List<string> names = keys
            .Select(key => progress.Topics?.FirstOrDefault(topic => topic.Key == key)?.Label
                ?? (progress.ActiveTopic?.Key == key ? progress.ActiveTopic.Label : null)
                ?? key.Replace('_', ' '))
            .ToList();
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    private static string Details(OrientationTopic topic) =>
        $"{Math.Min(topic.DetailsCovered, topic.DetailsTotal)} of {topic.DetailsTotal} details";
}
