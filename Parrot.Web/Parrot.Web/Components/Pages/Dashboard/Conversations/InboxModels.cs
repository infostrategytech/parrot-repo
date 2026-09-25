namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// The inbox's view-side vocabulary, ported from Aurora's ConversationModels.
/// Only the values Parrot can honour client-side are kept: the queue is
/// filtered and sorted over the loaded list because Parrot has no filter or
/// search endpoint.
/// </summary>

public enum InboxScope
{
    All,
    Mine,
    Unassigned
}

public enum ConversationSort
{
    Newest,
    Oldest
}

public enum ComposerMode
{
    Reply,
    Note
}

/// <summary>Mutable because these values are controlled by the inbox filters.</summary>
public sealed class ConversationFilters
{
    public string Search { get; set; } = "";
    public string? Channel { get; set; }
    public InboxScope Scope { get; set; } = InboxScope.All;
    public ConversationSort Sort { get; set; } = ConversationSort.Newest;
}
