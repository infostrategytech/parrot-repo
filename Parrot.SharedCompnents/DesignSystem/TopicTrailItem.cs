namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Where a topic stands in a conversation: still to come, being talked about, or done.</summary>
public enum TopicState { Upcoming, Current, Done }

/// <summary>One topic of a conversation, by the name the person sees ("Refunds").</summary>
public sealed record TopicTrailItem(string Title, TopicState State);
