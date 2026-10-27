using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Application.Tasks;
using Parrot.Domain.Entities;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// The workspace's tasks as the API keeps them, read and written with the session's token. A task is
/// named "T-12" here, after its number in the workspace, which is also how the API addresses it.
/// Failures throw the ApiException the screens already treat as "try again", so nothing looks saved
/// that is not; every change that saves tells the screens to read the list again.
/// </summary>
public sealed class ApiTaskStore(HttpClient http, AuthState auth, CustomerClient customers) : ITaskStore
{
    private const string Prefix = "T-";

    // Enough of the most recent customers to pick from; the rest are a search away in Customers.
    private const int CustomersOffered = 100;

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    private TaskPerson? _me;

    public bool Available => true;

    /// <summary>The person signed in: their name once the team has been read, their email until then.</summary>
    public TaskPerson Me => _me ?? new(auth.CurrentUser?.UserId ?? "", auth.CurrentUser?.Email ?? "");

    public event Action? Changed;

    public async Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await RequestAsync<List<TaskResponse>>(HttpMethod.Get, "api/tasks", null, cancellationToken) ?? [];
        return tasks.Select(ToTask).ToList();
    }

    public async Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken cancellationToken = default)
    {
        var team = await RequestAsync<TaskTeamResponse>(HttpMethod.Get, "api/tasks/team", null, cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
        _me = new TaskPerson(team.Me.Id, team.Me.Name);
        return team.People.Select(p => new TaskPerson(p.Id, p.Name)).ToList();
    }

    /// <summary>Each of the latest customers once per conversation they have with the business.</summary>
    public async Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken cancellationToken = default)
    {
        var page = await customers.ListAsync(null, null, null, 0, CustomersOffered, cancellationToken);
        return page.Items
            .SelectMany(customer => customer.Identities.Select(identity => new TaskCustomer(
                customer.Name,
                identity.Platform,
                identity.Username is { Length: > 0 } username ? "@" + username : identity.Sender,
                ConversationId(identity.ConnectionId, identity.Sender))))
            .ToList();
    }

    // Products & Services has no API the web app reads yet, so there is nothing to pick from.
    public Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskItem>>([]);

    public async Task<TaskRequest> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        var saved = await RequestAsync<TaskResponse>(HttpMethod.Post, "api/tasks", ToRequest(draft), cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
        Changed?.Invoke();
        return ToTask(saved);
    }

    public Task EditAsync(string id, TaskDraft draft, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Put, Path(id), ToRequest(draft), cancellationToken);

    public Task SetStateAsync(string id, TaskState state, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Put, Path(id) + "/state", new SetTaskStateRequest(state.ToString()), cancellationToken);

    public Task SetUrgentAsync(string id, bool urgent, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Put, Path(id) + "/urgent", new SetTaskUrgentRequest(urgent), cancellationToken);

    public Task SetDueAsync(string id, DateTime? dueAt, bool hasTime, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Put, Path(id) + "/due", new SetTaskDueRequest(dueAt, hasTime), cancellationToken);

    public Task AssignAsync(string id, TaskPerson? assignee, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Put, Path(id) + "/assignee", new AssignTaskRequest(assignee?.Id), cancellationToken);

    public Task PostUpdateAsync(string id, string text, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Post, Path(id) + "/notes", new PostTaskNoteRequest(text), cancellationToken);

    private async Task ChangeAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        await RequestAsync<TaskResponse>(method, path, body, cancellationToken);
        Changed?.Invoke();
    }

    private static string Path(string id) =>
        id.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(id[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"api/tasks/{number}"
            : throw new ArgumentException($"\"{id}\" doesn't name a task.", nameof(id));

    // The Inbox names a conversation "{connection}:{sender}"; a connection id has no colon in it.
    private static string ConversationId(Guid connectionId, string sender) => $"{connectionId}:{sender}";

    private static (Guid? ConnectionId, string? Sender) Conversation(string? conversationId)
    {
        if (conversationId is null) return (null, null);
        var colon = conversationId.IndexOf(':');
        return colon > 0 && Guid.TryParse(conversationId[..colon], out var connection)
            ? (connection, conversationId[(colon + 1)..])
            : (null, null);
    }

    private static SaveTaskRequest ToRequest(TaskDraft draft)
    {
        TaskCustomerDto? customer = null;
        if (draft.Customer is { } chosen)
        {
            var (connectionId, sender) = Conversation(chosen.ConversationId);
            customer = new TaskCustomerDto(chosen.Name, chosen.Channel, chosen.Contact, connectionId, sender);
        }

        return new SaveTaskRequest(
            draft.Kind.ToString(),
            draft.Title,
            draft.Summary,
            customer,
            draft.Address,
            draft.Lines?.Select(line => new WorkTaskLine(line.Name, line.Quantity, line.Price, line.Note, line.Charge)).ToList(),
            draft.Total,
            draft.Urgent,
            draft.DueAt,
            draft.DueHasTime,
            draft.Assignee?.Id);
    }

    private static TaskRequest ToTask(TaskResponse t) => new()
    {
        Id = Prefix + t.Number.ToString(CultureInfo.InvariantCulture),
        Kind = Enum.TryParse<TaskKind>(t.Kind, out var kind) ? kind : TaskKind.Other,
        Title = t.Title,
        Summary = t.Summary,
        Customer = t.Customer is { } c
            ? new TaskCustomer(c.Name, c.Channel ?? "", c.Contact, c.ConnectionId is { } connection && c.Sender is { } sender ? ConversationId(connection, sender) : null)
            : null,
        Address = t.Address,
        RaisedBy = t.RaisedBy,
        RaisedById = t.RaisedById,
        RaisedByAgent = t.RaisedByAgent,
        RaisedAt = t.RaisedAt,
        RaisedInMessageId = t.RaisedInMessageId,
        Assignee = t.Assignee is { } a ? new TaskPerson(a.Id, a.Name) : null,
        State = Enum.TryParse<TaskState>(t.State, out var state) ? state : TaskState.Open,
        Urgent = t.Urgent,
        DueAt = t.DueAt,
        DueHasTime = t.DueHasTime,
        Lines = t.Lines.Select(line => new TaskLine(line.Name, line.Quantity, line.Price, line.Note, line.Charge)).ToList(),
        Total = t.Total,
        History = t.History.Select(e => new TaskEvent(e.At, TaskWords.What(e), e.Who, e.Action == WorkTaskActions.Note ? e.Note ?? "" : null)).ToList(),
        LastActivityAt = t.LastActivityAt,
    };

    private async Task<T?> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to work on tasks.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: WriteOptions);
        using var response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }
}
