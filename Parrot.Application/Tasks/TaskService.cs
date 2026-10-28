using Microsoft.AspNetCore.Identity;
using Parrot.Application.Agents;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Tasks;

/// <summary>
/// The workspace's tasks: what needs doing for customers, raised by agents or
/// added by the team, and worked by the people whose role covers
/// conversations. Every change is written to the task's history here, by who
/// made it, so the trail is the server's account rather than a screen's.
/// </summary>
public sealed class TaskService(
    IWorkTaskRepository tasks,
    IWorkspaceRepository workspaces,
    ITeamRepository team,
    IWorkspaceAccessService access,
    IWorkspaceConnectionDirectory connections,
    IAgentRepository agents,
    UserManager<ApplicationUser> users,
    TimeProvider clock)
{
    /// <summary>How many closed tasks the list carries: the latest, for the Closed view.</summary>
    public const int ClosedShown = 200;

    public const int MaxTitle = 200;
    public const int MaxSummary = 2000;
    public const int MaxAddress = 160;
    public const int MaxCustomerName = 120;
    public const int MaxContact = 64;
    public const int MaxLines = 50;
    public const int MaxLineName = 120;
    public const int MaxLineNote = 300;
    public const int MaxQuantity = 9999;
    public const int MaxNote = 1000;
    public const decimal MaxAmount = 1_000_000_000_000m;

    /// <summary>
    /// Working tasks (adding, changing, being given one) is for whoever works
    /// conversations. The Analyst's task reports open the list to read only.
    /// </summary>
    public static bool CanWork(WorkspaceAccess access) =>
        access.Can(WorkspacePermissions.ConversationsView) || access.Can(WorkspacePermissions.ConversationsTakeOver);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<List<TaskResponse>> ListAsync(string workspaceId, IReadOnlySet<Guid>? reach, CancellationToken ct)
    {
        List<WorkTask> found = await tasks.ListAsync(workspaceId, reach, ClosedShown, ct);
        return [.. found.Select(ToResponse)];
    }

    /// <summary>The owner, then the team members whose access to this workspace lets them work tasks.</summary>
    public async Task<TaskTeamResponse> TeamAsync(string workspaceId, string userId, CancellationToken ct)
    {
        List<TaskPersonResponse> people = await PeopleAsync(workspaceId);
        TaskPersonResponse me = people.FirstOrDefault(p => p.Id == userId) ?? await PersonAsync(userId);
        return new TaskTeamResponse(me, people);
    }

    public async Task<TaskResponse> CreateAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, SaveTaskRequest request, CancellationToken ct)
    {
        TaskPersonResponse me = await PersonAsync(userId);
        WorkTask task = new()
        {
            WorkspaceId = workspaceId,
            Title = "",
            RaisedBy = me.Name,
            RaisedById = me.Id,
            RaisedAt = Now,
            LastActivityAt = Now,
        };
        await ApplyEnteredAsync(task, request, workspaceId, reach);

        if (request.DueAt is { } due) (task.DueAt, task.DueHasTime) = (Utc(due), request.DueHasTime);
        task.Urgent = request.Urgent;
        if (!string.IsNullOrWhiteSpace(request.AssigneeId))
        {
            TaskPersonResponse assignee = await AssigneeAsync(workspaceId, request.AssigneeId);
            (task.AssigneeId, task.AssigneeName) = (assignee.Id, assignee.Name);
        }

        task.History.Add(Event(WorkTaskActions.Added, me));
        await tasks.AddAsync(task, ct);
        return ToResponse(task);
    }

    /// <summary>A task an agent raised from a conversation in the workspace.</summary>
    public async Task<TaskResponse> RaiseAsync(string workspaceId, RaiseTaskRequest request, CancellationToken ct)
    {
        Agent? agent = await agents.GetByIdAsync(request.AgentId);
        if (agent is null || agent.WorkspaceId != workspaceId)
            throw new ValidationException("The agent raising the task is not one of the workspace's.");

        TaskCustomerDto customer = new(
            string.IsNullOrWhiteSpace(request.CustomerName) ? request.Sender : request.CustomerName,
            null,
            request.CustomerContact,
            request.ConnectionId,
            request.Sender);

        WorkTask task = new()
        {
            WorkspaceId = workspaceId,
            Title = "",
            RaisedBy = agent.Name,
            RaisedById = agent.Id.ToString(),
            RaisedByAgent = true,
            RaisedAt = Now,
            RaisedInMessageId = Trimmed(request.RaisedInMessageId),
            LastActivityAt = Now,
            Urgent = request.Urgent,
        };
        SaveTaskRequest entered = new(request.Kind, request.Title, request.Summary, customer, request.Address, request.Lines, request.Total);
        await ApplyEnteredAsync(task, entered, workspaceId, reach: null);
        if (request.DueAt is { } due) (task.DueAt, task.DueHasTime) = (Utc(due), request.DueHasTime);

        task.History.Add(new WorkTaskEvent
        {
            Action = WorkTaskActions.Raised,
            Who = agent.Name,
            WhoId = agent.Id.ToString(),
            At = Now,
            Value = task.CustomerName,
        });
        await tasks.AddAsync(task, ct);
        return ToResponse(task);
    }

    public async Task<TaskResponse> EditAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, SaveTaskRequest request, CancellationToken ct)
    {
        TaskPersonResponse me = await PersonAsync(userId);
        // Checked before the change, as it reads the workspace's connections.
        WorkTask draft = new() { WorkspaceId = workspaceId, Title = "", RaisedBy = "" };
        await ApplyEnteredAsync(draft, request, workspaceId, reach);

        return await ChangeAsync(workspaceId, reach, number, task =>
        {
            task.Kind = draft.Kind;
            task.Title = draft.Title;
            task.Summary = draft.Summary;
            task.Address = draft.Address;
            task.Lines = draft.Lines;
            task.Total = draft.Total;
            task.CustomerName = draft.CustomerName;
            task.CustomerChannel = draft.CustomerChannel;
            task.CustomerContact = draft.CustomerContact;
            task.ConnectionId = draft.ConnectionId;
            task.CustomerSender = draft.CustomerSender;
            return Event(WorkTaskActions.Edited, me);
        }, ct);
    }

    public async Task<TaskResponse> SetStateAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, string state, CancellationToken ct)
    {
        if (!Enum.TryParse(state, ignoreCase: true, out WorkTaskState parsed) || !Enum.IsDefined(parsed))
            throw new ValidationException("That isn't a status a task can have.");
        TaskPersonResponse me = await PersonAsync(userId);

        return await ChangeAsync(workspaceId, reach, number, task =>
        {
            if (task.State == parsed) return null;
            task.State = parsed;
            task.ClosedAt = task.IsClosed ? Now : null;
            return Event(WorkTaskActions.State, me, parsed.ToString());
        }, ct);
    }

    public async Task<TaskResponse> SetUrgentAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, bool urgent, CancellationToken ct)
    {
        TaskPersonResponse me = await PersonAsync(userId);
        return await ChangeAsync(workspaceId, reach, number, task =>
        {
            if (task.Urgent == urgent) return null;
            task.Urgent = urgent;
            return Event(WorkTaskActions.Urgent, me, urgent ? "on" : "off");
        }, ct);
    }

    public async Task<TaskResponse> SetDueAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, DateTime? dueAt, bool hasTime, CancellationToken ct)
    {
        TaskPersonResponse me = await PersonAsync(userId);
        DateTime? due = dueAt is { } at ? Utc(at) : null;
        bool withTime = due is not null && hasTime;
        return await ChangeAsync(workspaceId, reach, number, task =>
        {
            if (task.DueAt == due && task.DueHasTime == withTime) return null;
            (task.DueAt, task.DueHasTime) = (due, withTime);
            return Event(WorkTaskActions.Due, me, due is null ? "cleared" : "set");
        }, ct);
    }

    public async Task<TaskResponse> AssignAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, string? assigneeId, CancellationToken ct)
    {
        TaskPersonResponse me = await PersonAsync(userId);
        TaskPersonResponse? assignee = string.IsNullOrWhiteSpace(assigneeId) ? null : await AssigneeAsync(workspaceId, assigneeId);

        return await ChangeAsync(workspaceId, reach, number, task =>
        {
            if (task.AssigneeId == assignee?.Id) return null;
            (task.AssigneeId, task.AssigneeName) = (assignee?.Id, assignee?.Name);
            return assignee is null
                ? Event(WorkTaskActions.Unassigned, me)
                : Event(WorkTaskActions.Assigned, me, assignee.Name, assignee.Id);
        }, ct);
    }

    public async Task<TaskResponse> PostNoteAsync(string workspaceId, string userId, IReadOnlySet<Guid>? reach, int number, string text, CancellationToken ct)
    {
        string note = text?.Trim() ?? "";
        if (note.Length == 0) throw new ValidationException("Write the note first.");
        if (note.Length > MaxNote) throw new ValidationException($"A note can be up to {MaxNote} characters.");
        TaskPersonResponse me = await PersonAsync(userId);

        return await ChangeAsync(workspaceId, reach, number, _ =>
        {
            WorkTaskEvent entry = Event(WorkTaskActions.Note, me);
            entry.Note = note;
            return entry;
        }, ct);
    }

    // Loads, checks the person's channels reach it, changes and saves. A task
    // out of reach is not found, as one that does not exist.
    private async Task<TaskResponse> ChangeAsync(string workspaceId, IReadOnlySet<Guid>? reach, int number, Func<WorkTask, WorkTaskEvent?> change, CancellationToken ct)
    {
        WorkTask? task = await tasks.ChangeAsync(workspaceId, number, found =>
        {
            if (!Reaches(found, reach)) throw new NotFoundException("Task not found.");
            WorkTaskEvent? entry = change(found);
            if (entry is not null) found.LastActivityAt = entry.At;
            return entry;
        }, ct);
        return task is null ? throw new NotFoundException("Task not found.") : ToResponse(task);
    }

    private static bool Reaches(WorkTask task, IReadOnlySet<Guid>? reach) =>
        reach is null || task.ConnectionId is not { } connection || reach.Contains(connection);

    // What a person enters, checked and tidied onto the task.
    private async Task ApplyEnteredAsync(WorkTask task, SaveTaskRequest request, string workspaceId, IReadOnlySet<Guid>? reach)
    {
        if (!Enum.TryParse(request.Kind, ignoreCase: true, out WorkTaskKind kind) || !Enum.IsDefined(kind))
            throw new ValidationException("That isn't a type a task can have.");

        string title = request.Title?.Trim() ?? "";
        if (title.Length == 0) throw new ValidationException("Say what needs doing.");
        if (title.Length > MaxTitle) throw new ValidationException($"Keep what needs doing to {MaxTitle} characters; put the rest in the details.");

        string? summary = Trimmed(request.Summary);
        if (summary?.Length > MaxSummary) throw new ValidationException($"The details can be up to {MaxSummary} characters.");

        string? address = Trimmed(request.Address);
        if (address?.Length > MaxAddress) throw new ValidationException($"The address can be up to {MaxAddress} characters.");

        List<WorkTaskLine> lines = Lines(request.Lines);
        decimal? total = lines.Any(l => l.Price is not null) ? lines.Sum(l => (l.Price ?? 0) * l.Quantity) : request.Total;
        if (total is < 0 or > MaxAmount) throw new ValidationException("The total isn't an amount a task can have.");

        task.Kind = kind;
        task.Title = title;
        task.Summary = summary;
        task.Address = address;
        task.Lines = lines;
        task.Total = total;
        (task.CustomerName, task.CustomerChannel, task.CustomerContact, task.ConnectionId, task.CustomerSender) =
            (null, null, null, null, null);

        if (request.Customer is not { } customer) return;

        string name = customer.Name?.Trim() ?? "";
        if (name.Length == 0) throw new ValidationException("Give the customer's name.");
        if (name.Length > MaxCustomerName) throw new ValidationException($"The customer's name can be up to {MaxCustomerName} characters.");
        string? contact = Trimmed(customer.Contact);
        if (contact?.Length > MaxContact) throw new ValidationException($"The customer's number can be up to {MaxContact} characters.");

        task.CustomerName = name;
        task.CustomerContact = contact;
        task.CustomerChannel = "";

        if (customer.ConnectionId is not { } connectionId) return;
        string? sender = Trimmed(customer.Sender);
        if (sender is null || sender.Length > 256) throw new ValidationException("Say which conversation the task is for.");

        // The conversation has to be the workspace's, and on a channel the person reaches.
        IReadOnlyList<(Guid Id, MessagingPlatform Platform)> workspaceConnections = await connections.ListAsync(workspaceId);
        if (!workspaceConnections.Any(c => c.Id == connectionId) || reach is not null && !reach.Contains(connectionId))
            throw new NotFoundException("Conversation not found.");

        task.ConnectionId = connectionId;
        task.CustomerSender = sender;
        task.CustomerChannel = WorkspaceChannels.KeyOf(workspaceConnections.First(c => c.Id == connectionId).Platform);
    }

    private static List<WorkTaskLine> Lines(List<WorkTaskLine>? raw)
    {
        List<WorkTaskLine> lines = [];
        foreach (WorkTaskLine line in raw ?? [])
        {
            string name = line.Name?.Trim() ?? "";
            if (name.Length == 0) continue;
            if (name.Length > MaxLineName) throw new ValidationException($"An item's name can be up to {MaxLineName} characters.");
            if (line.Quantity is < 1 or > MaxQuantity) throw new ValidationException($"A quantity is from 1 to {MaxQuantity}.");
            if (line.Price is < 0 or > MaxAmount) throw new ValidationException("A price can't be below zero.");
            string? note = Trimmed(line.Note);
            if (note?.Length > MaxLineNote) throw new ValidationException($"An item's note can be up to {MaxLineNote} characters.");
            lines.Add(new WorkTaskLine(name, line.Charge ? 1 : line.Quantity, line.Price, note, line.Charge));
        }

        return lines.Count > MaxLines ? throw new ValidationException($"A task can list up to {MaxLines} items.") : lines;
    }

    private async Task<TaskPersonResponse> AssigneeAsync(string workspaceId, string assigneeId) =>
        (await PeopleAsync(workspaceId)).FirstOrDefault(p => p.Id == assigneeId)
        ?? throw new ValidationException("That person can't be given tasks in this workspace.");

    private async Task<List<TaskPersonResponse>> PeopleAsync(string workspaceId)
    {
        Workspace workspace = await workspaces.GetByIdAsync(workspaceId)
            ?? throw new NotFoundException("Workspace not found.");

        List<string> ids = [workspace.OwnerId];
        foreach (WorkspaceGrant grant in await team.ListTeamGrantsAsync(workspace.OwnerId))
        {
            if (grant.WorkspaceId is not null && grant.WorkspaceId != workspaceId || ids.Contains(grant.UserId)) continue;
            // Their access here, as every request reads it: their workspace grant or the all-workspaces one, and its role now.
            if (await access.GetAsync(grant.UserId, workspaceId) is { } granted && CanWork(granted)) ids.Add(grant.UserId);
        }

        List<TaskPersonResponse> people = [];
        foreach (string id in ids)
        {
            // An account deleted since it joined can't be given anything.
            if (await users.FindByIdAsync(id) is { } user) people.Add(new TaskPersonResponse(user.Id, NameOf(user)));
        }

        return people;
    }

    private async Task<TaskPersonResponse> PersonAsync(string userId) =>
        await users.FindByIdAsync(userId) is { } user
            ? new TaskPersonResponse(user.Id, NameOf(user))
            : throw new UnauthorizedException("Sign in to continue.");

    private static string NameOf(ApplicationUser user)
    {
        string name = $"{user.FirstName} {user.LastName}".Trim();
        return name.Length > 0 ? name : user.Email ?? user.UserName ?? "Someone";
    }

    private WorkTaskEvent Event(string action, TaskPersonResponse who, string? value = null, string? targetId = null) =>
        new() { Action = action, Who = who.Name, WhoId = who.Id, At = Now, Value = value, TargetId = targetId };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static TaskResponse ToResponse(WorkTask t) => new(
        t.Id,
        t.Number,
        t.Kind.ToString(),
        t.Title,
        t.Summary,
        t.CustomerName is null ? null : new TaskCustomerDto(t.CustomerName, t.CustomerChannel, t.CustomerContact, t.ConnectionId, t.CustomerSender),
        t.Address,
        t.RaisedBy,
        t.RaisedById,
        t.RaisedByAgent,
        t.RaisedAt,
        t.RaisedInMessageId,
        t.AssigneeId is null ? null : new TaskPersonResponse(t.AssigneeId, t.AssigneeName ?? ""),
        t.State.ToString(),
        t.Urgent,
        t.DueAt,
        t.DueHasTime,
        t.Lines,
        t.Total,
        [.. t.History.OrderByDescending(e => e.At).Select(e => new TaskEventResponse(e.At, e.Action, e.Who, e.WhoId, e.Value, e.TargetId, e.Note))],
        t.LastActivityAt);
}
