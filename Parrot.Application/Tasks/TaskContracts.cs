using Parrot.Domain.Entities;

namespace Parrot.Application.Tasks;

/// <summary>
/// The customer a task is for: from a conversation (its connection and the
/// customer's sender id), or someone the Inbox doesn't know, with a name and a number.
/// </summary>
public sealed record TaskCustomerDto(string Name, string? Channel, string? Contact, Guid? ConnectionId, string? Sender);

/// <summary>A person on the team a task can be given to.</summary>
public sealed record TaskPersonResponse(string Id, string Name);

/// <summary>Something that happened to a task. <paramref name="Action"/> is one of <see cref="WorkTaskActions"/>.</summary>
public sealed record TaskEventResponse(
    DateTime At,
    string Action,
    string Who,
    string? WhoId,
    string? Value,
    string? TargetId,
    string? Note);

/// <summary>
/// A task as the screens read it. <paramref name="Kind"/> and <paramref name="State"/>
/// are the names of <see cref="WorkTaskKind"/> and <see cref="WorkTaskState"/>; the
/// history is newest first.
/// </summary>
public sealed record TaskResponse(
    Guid Id,
    int Number,
    string Kind,
    string Title,
    string? Summary,
    TaskCustomerDto? Customer,
    string? Address,
    string RaisedBy,
    string? RaisedById,
    bool RaisedByAgent,
    DateTime RaisedAt,
    string? RaisedInMessageId,
    TaskPersonResponse? Assignee,
    string State,
    bool Urgent,
    DateTime? DueAt,
    bool DueHasTime,
    List<WorkTaskLine> Lines,
    decimal? Total,
    List<TaskEventResponse> History,
    DateTime LastActivityAt);

/// <summary>Who tasks can be given to in the workspace, and which of them is asking.</summary>
public sealed record TaskTeamResponse(TaskPersonResponse Me, List<TaskPersonResponse> People);

/// <summary>
/// What a person enters for a task, when adding it and when editing it. Editing
/// changes what was entered (title, kind, what's needed, customer, where, lines,
/// total); urgency, due date and assignee have their own changes.
/// </summary>
public sealed record SaveTaskRequest(
    string Kind,
    string Title,
    string? Summary = null,
    TaskCustomerDto? Customer = null,
    string? Address = null,
    List<WorkTaskLine>? Lines = null,
    decimal? Total = null,
    bool Urgent = false,
    DateTime? DueAt = null,
    bool DueHasTime = false,
    string? AssigneeId = null);

/// <summary>
/// A task an agent raises from a conversation it is answering. The customer's
/// name and contact are as the agent knows them. The agent is named as Omni
/// names it (agt_…) and, if it says, by its name; neither is looked up, as
/// agents live in Omni. The conversation must be the workspace's.
/// </summary>
public sealed record RaiseTaskRequest(
    string? AgentId,
    string Kind,
    string Title,
    Guid ConnectionId,
    string Sender,
    string? CustomerName = null,
    string? CustomerContact = null,
    string? Summary = null,
    string? RaisedInMessageId = null,
    string? Address = null,
    List<WorkTaskLine>? Lines = null,
    decimal? Total = null,
    bool Urgent = false,
    DateTime? DueAt = null,
    bool DueHasTime = false,
    string? AgentName = null);

public sealed record SetTaskStateRequest(string State);

public sealed record SetTaskUrgentRequest(bool Urgent);

public sealed record SetTaskDueRequest(DateTime? DueAt, bool HasTime);

/// <summary>Null to unassign.</summary>
public sealed record AssignTaskRequest(string? AssigneeId);

public sealed record PostTaskNoteRequest(string Text);
