namespace Parrot.Application.Conversations;

public sealed class OutboundSettings
{
    public const string SectionName = "Outbound";

    /// <summary>
    /// True: a picture or file is stored, the reply is recorded as pending, and a job on the outbound topic has
    /// the send worker hand it to the channel; the API answers at once. False (the default): the API sends it
    /// before it answers. Turn it on only when the send worker runs.
    /// </summary>
    public bool DeferFileSends { get; init; }
}
