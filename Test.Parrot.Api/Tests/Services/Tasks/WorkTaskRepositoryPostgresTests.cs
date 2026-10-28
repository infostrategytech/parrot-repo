using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Tasks;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Tasks;

// The tasks against a real PostgreSQL: numbered per workspace without gaps or
// repeats when added at once, and a change saved with its new history entry.
// Run against a disposable database: see PARROT_TEST_POSTGRES.
public sealed class WorkTaskRepositoryPostgresTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<ApplicationDbContext>? _options;

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner@example.com", Email = "owner@example.com" });
        db.Workspaces.AddRange(new Workspace { Id = "ws-1", OwnerId = "owner" }, new Workspace { Id = "ws-2", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private EfWorkTaskRepository Repository() => new(new Contexts(_options!));

    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private static WorkTask NewTask(string workspaceId, string title = "Send Kemi a quote", Guid? connectionId = null) => new()
    {
        WorkspaceId = workspaceId,
        Title = title,
        RaisedBy = "Ngozi",
        ConnectionId = connectionId,
        Lines = [new WorkTaskLine("Jollof tray", 2, 15000m), new WorkTaskLine("Delivery", 1, 2500m, Charge: true)],
        History = [new WorkTaskEvent { Action = WorkTaskActions.Added, Who = "Ngozi" }],
    };

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task TasksAddedAtOnce_AreNumberedOneUp_PerWorkspace()
    {
        EfWorkTaskRepository repository = Repository();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => repository.AddAsync(NewTask("ws-1", $"Task {i}"), default)));
        await repository.AddAsync(NewTask("ws-2"), default);

        List<WorkTask> first = await repository.ListAsync("ws-1", null, 10, default);
        Assert.Equal(Enumerable.Range(1, 8), first.Select(t => t.Number).Order());
        WorkTask other = Assert.Single(await repository.ListAsync("ws-2", null, 10, default));
        Assert.Equal(1, other.Number);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task AChange_IsSavedWithItsNewHistoryEntry_AndTheLinesComeBackWhole()
    {
        EfWorkTaskRepository repository = Repository();
        await repository.AddAsync(NewTask("ws-1"), default);

        WorkTask? changed = await repository.ChangeAsync("ws-1", 1, task =>
        {
            task.State = WorkTaskState.Done;
            task.ClosedAt = DateTime.UtcNow;
            return new WorkTaskEvent { Action = WorkTaskActions.State, Who = "Ngozi", Value = "Done" };
        }, default);

        Assert.NotNull(changed);
        WorkTask read = Assert.Single(await repository.ListAsync("ws-1", null, 10, default));
        Assert.Equal(WorkTaskState.Done, read.State);
        Assert.Equal([WorkTaskActions.Added, WorkTaskActions.State], read.History.OrderBy(e => e.At).Select(e => e.Action));
        Assert.Equal(2, read.Lines.Count);
        Assert.True(read.Lines[1].Charge);
        Assert.Null(await repository.ChangeAsync("ws-2", 1, _ => null, default));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task List_KeepsAllOpen_TheLatestClosed_AndOnlyTheChannelsReached()
    {
        EfWorkTaskRepository repository = Repository();
        Guid reached = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        await repository.AddAsync(NewTask("ws-1", "Reached", reached), default);
        await repository.AddAsync(NewTask("ws-1", "Other channel", other), default);
        await repository.AddAsync(NewTask("ws-1", "Walk-in"), default);
        for (int i = 0; i < 3; i++)
        {
            await repository.AddAsync(NewTask("ws-1", $"Closed {i}"), default);
            int number = 4 + i;
            await repository.ChangeAsync("ws-1", number, task =>
            {
                (task.State, task.ClosedAt) = (WorkTaskState.Cancelled, DateTime.UtcNow.AddMinutes(number));
                return null;
            }, default);
        }

        List<WorkTask> seen = await repository.ListAsync("ws-1", new HashSet<Guid> { reached }, closedLimit: 2, default);

        Assert.Equal(["Closed 1", "Closed 2", "Reached", "Walk-in"], seen.Select(t => t.Title).Order());
    }
}
