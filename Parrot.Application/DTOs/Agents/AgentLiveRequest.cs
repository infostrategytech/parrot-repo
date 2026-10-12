namespace Parrot.Application.DTOs.Agents;

/// <summary>The body of the live switch: on, it starts answering where it is set to; off, it stops.</summary>
public sealed record AgentLiveRequest(bool On);
