using Microsoft.EntityFrameworkCore;
using Parrot.Application.Tasks;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Tasks;

public sealed class EfWorkTaskRepository(IDbContextFactory<ApplicationDbContext> factory) : IWorkTaskRepository
{
    public async Task<List<WorkTask>> ListAsync(string workspaceId, IReadOnlySet<Guid>? connections, int closedLimit, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);

        IQueryable<WorkTask> query = db.WorkTasks.AsNoTracking().Where(t => t.WorkspaceId == workspaceId);

        // A team member limited to some channels sees the tasks from them, and those from no conversation.
        if (connections is not null)
        {
            List<Guid> reachable = [.. connections];
            query = query.Where(t => t.ConnectionId == null || reachable.Contains(t.ConnectionId.Value));
        }

        List<WorkTask> open = await query
            .Where(t => t.State != WorkTaskState.Done && t.State != WorkTaskState.Cancelled)
            .Include(t => t.History)
            .AsSplitQuery()
            .ToListAsync(ct);

        List<WorkTask> closed = await query
            .Where(t => t.State == WorkTaskState.Done || t.State == WorkTaskState.Cancelled)
            .OrderByDescending(t => t.ClosedAt)
            .Take(closedLimit)
            .Include(t => t.History)
            .AsSplitQuery()
            .ToListAsync(ct);

        return [.. open, .. closed];
    }

    public async Task AddAsync(WorkTask task, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

        // One task numbered at a time per workspace, until this transaction ends:
        // the next number is then always free, and the unique index only a backstop.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"work-task:" + task.WorkspaceId}))", ct);
        int last = await db.WorkTasks.Where(t => t.WorkspaceId == task.WorkspaceId).MaxAsync(t => (int?)t.Number, ct) ?? 0;
        task.Number = last + 1;
        foreach (WorkTaskEvent entry in task.History) entry.TaskId = task.Id;
        db.WorkTasks.Add(task);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<WorkTask?> ChangeAsync(string workspaceId, int number, Func<WorkTask, WorkTaskEvent?> change, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        WorkTask? task = await db.WorkTasks
            .Include(t => t.History)
            .FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.Number == number, ct);
        if (task is null) return null;

        WorkTaskEvent? entry = change(task);
        if (entry is not null)
        {
            // Added through its own set: a new child with its key already set would otherwise be taken for an existing row.
            entry.TaskId = task.Id;
            db.WorkTaskEvents.Add(entry);
        }

        await db.SaveChangesAsync(ct);
        return task;
    }
}
