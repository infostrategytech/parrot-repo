using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Workspaces;

/// <summary>Workspaces in a dictionary, with the same duplicate-id answer the database gives.</summary>
public sealed class FakeWorkspaceRepository : IWorkspaceRepository
{
    public Dictionary<string, Workspace> Rows { get; } = [];

    public int Updates { get; private set; }

    public Workspace Seed(string id, string ownerId, string? name = null, string? logoUrl = null, DateTime? createdAt = null)
    {
        Workspace workspace = new()
        {
            Id = id,
            OwnerId = ownerId,
            Name = name,
            LogoUrl = logoUrl,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        Rows[id] = workspace;
        return workspace;
    }

    public Task<Workspace?> GetByIdAsync(string id) =>
        Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId) =>
        Task.FromResult<IReadOnlyList<Workspace>>(
            Rows.Values.Where(w => w.OwnerId == ownerId).OrderBy(w => w.CreatedAt).ToList());

    public Task<bool> TryAddAsync(Workspace workspace) =>
        Task.FromResult(Rows.TryAdd(workspace.Id, workspace));

    public Task UpdateAsync(Workspace workspace)
    {
        Rows[workspace.Id] = workspace;
        Updates++;
        return Task.CompletedTask;
    }
}
