namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// The inbox's view-side vocabulary, ported from Aurora's ConversationModels.
/// The queue is filtered and sorted over the loaded list because Parrot has no
/// filter or search endpoint.
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
    Oldest,
    Priority
}

public enum ComposerMode
{
    Reply,
    Note
}

public enum ConversationStatus
{
    Open,
    Pending,
    Resolved
}

public enum ConversationPriority
{
    Normal,
    High,
    Urgent
}

/// <summary>Who answers the customer: the AI agent, a person who took over, or nobody while the AI is paused.</summary>
public enum AiState
{
    Active,
    Handoff,
    Paused
}

public enum MessageKind
{
    Customer,
    Ai,
    Human,
    Note,
    System,
    AiQuestion
}

/// <summary>Mutable because these values are controlled by the inbox filters.</summary>
public sealed class ConversationFilters
{
    public string Search { get; set; } = "";
    public string? Channel { get; set; }
    public ConversationStatus? Status { get; set; }
    public string? AgentId { get; set; }
    public InboxScope Scope { get; set; } = InboxScope.All;
    public ConversationSort Sort { get; set; } = ConversationSort.Newest;
}
