using Microsoft.AspNetCore.Identity;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Models;
using Parrot.Application.Tasks;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Teams;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Tasks;

// TaskService over in-memory fakes: what a task may name, who sees it, who can be
// given it, and that every change leaves an entry in its history.
public sealed class TaskServiceTests
{
    private const string Owner = "owner";
    private const string Ws = "ws-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly FakeConnectionDirectory _connections = new();
    private readonly FakeWorkTaskRepository _tasks = new();
    private readonly Mock<IAgentRepository> _agents = new();
    private readonly Mock<UserManager<ApplicationUser>> _users =
        new(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
    private readonly TaskService _service;
    private readonly Guid _whatsApp;
    private readonly Guid _instagram;

    public TaskServiceTests()
    {
        _workspaces.Seed(Ws, Owner);
        _workspaces.Seed("ws-2", Owner);
        _whatsApp = _connections.Add(Ws, MessagingPlatform.WhatsApp);
        _instagram = _connections.Add(Ws, MessagingPlatform.Instagram);
        User(Owner, "Ngozi", "Eze");
        _users.Setup(u => u.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => _people.GetValueOrDefault(id));

        _service = new TaskService(_tasks, _workspaces, _team, TeamFixture.Access(_workspaces, _team, _connections),
            _connections, _agents.Object, _users.Object, TimeProvider.System);
    }

    private readonly Dictionary<string, ApplicationUser> _people = [];

    private void User(string id, string first, string? last = null) =>
        _people[id] = new ApplicationUser { Id = id, FirstName = first, LastName = last, Email = $"{id}@example.com" };

    private static SaveTaskRequest Draft(string title = "Send Kemi a quote", TaskCustomerDto? customer = null, List<WorkTaskLine>? lines = null, decimal? total = null) =>
        new("Quote", title, Customer: customer, Lines: lines, Total: total);

    private TaskCustomerDto OnWhatsApp(string sender = "2348012345678") => new("Kemi", null, null, _whatsApp, sender);

    [Fact]
    public async Task Create_NumbersTasksInTheWorkspace_AndRecordsWhoAddedIt()
    {
        TaskResponse first = await _service.CreateAsync(Ws, Owner, null, Draft(), default);
        TaskResponse second = await _service.CreateAsync(Ws, Owner, null, Draft("Call Tunde back"), default);

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal("Ngozi Eze", first.RaisedBy);
        Assert.False(first.RaisedByAgent);
        TaskEventResponse added = Assert.Single(first.History);
        Assert.Equal(WorkTaskActions.Added, added.Action);
        Assert.Equal(Owner, added.WhoId);
    }

    [Fact]
    public async Task Create_ForAConversation_TakesTheChannelFromItsConnection()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(customer: OnWhatsApp()), default);

        Assert.Equal("whatsapp", task.Customer!.Channel);
        Assert.Equal(_whatsApp, task.Customer.ConnectionId);
        Assert.Equal("2348012345678", task.Customer.Sender);
    }

    [Fact]
    public async Task Create_ForAnotherWorkspacesConversation_IsNotFound()
    {
        Guid elsewhere = _connections.Add("ws-2", MessagingPlatform.WhatsApp);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateAsync(Ws, Owner, null, Draft(customer: new TaskCustomerDto("Kemi", null, null, elsewhere, "234")), default));
    }

    [Fact]
    public async Task Create_ForAConversationOutsideThePersonsChannels_IsNotFound()
    {
        HashSet<Guid> instagramOnly = [_instagram];

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateAsync(Ws, Owner, instagramOnly, Draft(customer: OnWhatsApp()), default));
    }

    [Fact]
    public async Task Create_WithPricedLines_TotalsThemWhateverAmountWasTyped()
    {
        List<WorkTaskLine> lines = [new("Jollof tray", 2, 15000m), new("Delivery", 1, 2500m, Charge: true), new("Napkins", 3)];

        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(lines: lines, total: 1m), default);

        Assert.Equal(32500m, task.Total);
        Assert.Equal(3, task.Lines.Count);
    }

    [Fact]
    public async Task Create_WithoutPrices_KeepsTheTypedAmount()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(lines: [new("Cake", 1)], total: 40000m), default);

        Assert.Equal(40000m, task.Total);
    }

    [Theory]
    [InlineData("Quote", "  ")]
    [InlineData("Lunch", "Send Kemi a quote")]
    public async Task Create_WithoutATitleOrWithAnUnknownKind_IsRefused(string kind, string title)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Ws, Owner, null, new SaveTaskRequest(kind, title), default));
        Assert.Empty(_tasks.Rows);
    }

    [Fact]
    public async Task List_ForAChannelLimitedMember_KeepsTheirChannelsAndWalkIns()
    {
        await _service.CreateAsync(Ws, Owner, null, Draft("On WhatsApp", OnWhatsApp()), default);
        await _service.CreateAsync(Ws, Owner, null, Draft("On Instagram", new TaskCustomerDto("Bisi", null, null, _instagram, "ig-1")), default);
        await _service.CreateAsync(Ws, Owner, null, Draft("Walk-in", new TaskCustomerDto("Femi", null, "0803", null, null)), default);

        List<TaskResponse> seen = await _service.ListAsync(Ws, new HashSet<Guid> { _instagram }, default);

        Assert.Equal(["On Instagram", "Walk-in"], seen.Select(t => t.Title).Order());
    }

    [Fact]
    public async Task Change_OutsideThePersonsChannels_IsNotFound_AndChangesNothing()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(customer: OnWhatsApp()), default);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.SetStateAsync(Ws, Owner, new HashSet<Guid> { _instagram }, task.Number, "Done", default));
        Assert.Equal(WorkTaskState.Open, _tasks.Rows.Single().State);
    }

    [Fact]
    public async Task SetState_Done_ClosesIt_AndSayingItAgainAddsNothing()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);

        TaskResponse done = await _service.SetStateAsync(Ws, Owner, null, task.Number, "done", default);
        TaskResponse again = await _service.SetStateAsync(Ws, Owner, null, task.Number, "Done", default);

        Assert.Equal("Done", done.State);
        Assert.NotNull(_tasks.Rows.Single().ClosedAt);
        Assert.Equal(2, again.History.Count);
        Assert.Equal(WorkTaskActions.State, again.History[0].Action);
        Assert.Equal("Done", again.History[0].Value);
    }

    [Fact]
    public async Task SetState_OpenedAgain_ClearsWhenItClosed()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);
        await _service.SetStateAsync(Ws, Owner, null, task.Number, "Cancelled", default);

        await _service.SetStateAsync(Ws, Owner, null, task.Number, "Open", default);

        Assert.Null(_tasks.Rows.Single().ClosedAt);
    }

    [Fact]
    public async Task Change_ToATaskThatIsNotThere_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.SetUrgentAsync(Ws, Owner, null, 41, true, default));
    }

    [Fact]
    public async Task Team_IsTheOwner_AndTheMembersWhoWorkConversationsHere()
    {
        User("ada", "Ada");
        User("ben", "Ben");
        User("cy", "Cy");
        User("dee", "Dee");
        _team.Grant(Owner, "ada", Ws, WorkspaceRoles.ConversationManager);
        _team.Grant(Owner, "ben", null, WorkspaceRoles.ConversationManager);
        _team.Grant(Owner, "cy", Ws, WorkspaceRoles.Analyst);
        _team.Grant(Owner, "dee", "ws-2", WorkspaceRoles.ConversationManager);

        TaskTeamResponse team = await _service.TeamAsync(Ws, "ada", default);

        Assert.Equal([Owner, "ada", "ben"], team.People.Select(p => p.Id));
        Assert.Equal(new TaskPersonResponse("ada", "Ada"), team.Me);
    }

    [Fact]
    public async Task Assign_ToSomeoneWhoCannotWorkTasks_IsRefused()
    {
        User("cy", "Cy");
        _team.Grant(Owner, "cy", Ws, WorkspaceRoles.Analyst);
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);

        await Assert.ThrowsAsync<ValidationException>(() => _service.AssignAsync(Ws, Owner, null, task.Number, "cy", default));
    }

    [Fact]
    public async Task Assign_RecordsWhoItWentTo_AndUnassignRecordsThatToo()
    {
        User("ada", "Ada", "Obi");
        _team.Grant(Owner, "ada", Ws, WorkspaceRoles.ConversationManager);
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);

        TaskResponse given = await _service.AssignAsync(Ws, Owner, null, task.Number, "ada", default);
        TaskResponse taken = await _service.AssignAsync(Ws, Owner, null, task.Number, null, default);

        Assert.Equal(new TaskPersonResponse("ada", "Ada Obi"), given.Assignee);
        Assert.Equal(WorkTaskActions.Assigned, given.History[0].Action);
        Assert.Equal("ada", given.History[0].TargetId);
        Assert.Null(taken.Assignee);
        Assert.Equal(WorkTaskActions.Unassigned, taken.History[0].Action);
    }

    [Fact]
    public async Task PostNote_KeepsItInTheHistory_AndAnEmptyOneIsRefused()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);

        await Assert.ThrowsAsync<ValidationException>(() => _service.PostNoteAsync(Ws, Owner, null, task.Number, "   ", default));
        TaskResponse noted = await _service.PostNoteAsync(Ws, Owner, null, task.Number, " Called her, she'll pay Friday ", default);

        Assert.Equal(WorkTaskActions.Note, noted.History[0].Action);
        Assert.Equal("Called her, she'll pay Friday", noted.History[0].Note);
    }

    [Fact]
    public async Task SetDue_WithoutADay_DropsTheTimeToo()
    {
        TaskResponse task = await _service.CreateAsync(Ws, Owner, null, Draft(), default);
        await _service.SetDueAsync(Ws, Owner, null, task.Number, new DateTime(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc), true, default);

        TaskResponse cleared = await _service.SetDueAsync(Ws, Owner, null, task.Number, null, true, default);

        Assert.Null(cleared.DueAt);
        Assert.False(cleared.DueHasTime);
        Assert.Equal("cleared", cleared.History[0].Value);
    }

    [Fact]
    public async Task Raise_ByAnAgentOfTheWorkspace_SaysWhoRaisedItAndForWhom()
    {
        Agent kora = new() { WorkspaceId = Ws, Name = "Kora", Description = "", MascotUrl = "" };
        _agents.Setup(a => a.GetByIdAsync(kora.Id)).ReturnsAsync(kora);

        TaskResponse task = await _service.RaiseAsync(Ws, new RaiseTaskRequest(kora.Id, "Order", "Confirm Kemi's order", _whatsApp, "234",
            CustomerName: "Kemi's Kitchen", RaisedInMessageId: "wamid.1"), default);

        Assert.True(task.RaisedByAgent);
        Assert.Equal("Kora", task.RaisedBy);
        Assert.Equal(kora.Id.ToString(), task.RaisedById);
        Assert.Equal("wamid.1", task.RaisedInMessageId);
        TaskEventResponse raised = Assert.Single(task.History);
        Assert.Equal(WorkTaskActions.Raised, raised.Action);
        Assert.Equal("Kemi's Kitchen", raised.Value);
    }

    [Fact]
    public async Task Raise_ByAnotherWorkspacesAgent_IsRefused()
    {
        Agent stranger = new() { WorkspaceId = "ws-2", Name = "Tolu", Description = "", MascotUrl = "" };
        _agents.Setup(a => a.GetByIdAsync(stranger.Id)).ReturnsAsync(stranger);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.RaiseAsync(Ws, new RaiseTaskRequest(stranger.Id, "Order", "Confirm", _whatsApp, "234"), default));
    }
}

/// <summary>Tasks in a list, numbered and changed as the database repository does.</summary>
public sealed class FakeWorkTaskRepository : IWorkTaskRepository
{
    public List<WorkTask> Rows { get; } = [];

    public Task<List<WorkTask>> ListAsync(string workspaceId, IReadOnlySet<Guid>? connections, int closedLimit, CancellationToken ct) =>
        Task.FromResult(Rows
            .Where(t => t.WorkspaceId == workspaceId)
            .Where(t => connections is null || t.ConnectionId is null || connections.Contains(t.ConnectionId.Value))
            .ToList());

    public Task AddAsync(WorkTask task, CancellationToken ct)
    {
        task.Number = Rows.Where(t => t.WorkspaceId == task.WorkspaceId).Select(t => t.Number).DefaultIfEmpty().Max() + 1;
        Rows.Add(task);
        return Task.CompletedTask;
    }

    public Task<WorkTask?> ChangeAsync(string workspaceId, int number, Func<WorkTask, WorkTaskEvent?> change, CancellationToken ct)
    {
        WorkTask? task = Rows.FirstOrDefault(t => t.WorkspaceId == workspaceId && t.Number == number);
        if (task is null) return Task.FromResult<WorkTask?>(null);
        WorkTaskEvent? entry = change(task);
        if (entry is not null) task.History.Add(entry);
        return Task.FromResult<WorkTask?>(task);
    }
}
