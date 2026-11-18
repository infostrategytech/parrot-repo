using Parrot.Application.Auth;
using Parrot.Application.BusinessSources;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Workspaces;

public class WorkspaceService : IWorkspaceService
{
    private readonly IWorkspaceRepository _repository;
    private readonly ITeamRepository _team;
    private readonly IWorkspaceAccessService _access;
    private readonly IConnectionSourceSyncService _sources;
    private readonly IAppLogger<WorkspaceService> _logger;

    public WorkspaceService(
        IWorkspaceRepository repository,
        ITeamRepository team,
        IWorkspaceAccessService access,
        IConnectionSourceSyncService sources,
        IAppLogger<WorkspaceService> logger)
    {
        _repository = repository;
        _team = team;
        _access = access;
        _sources = sources;
        _logger = logger;
    }

    // The owned ones first, oldest first, then the ones the person was granted.
    public async Task<IReadOnlyList<WorkspaceResponse>> ListAsync(string userId, string currentWorkspaceId, string? defaultWorkspaceId = null)
    {
        IReadOnlyList<Workspace> owned = await _repository.ListByOwnerAsync(userId);
        List<WorkspaceResponse> listed = owned.Select(w => ToResponse(w, currentWorkspaceId)).ToList();

        foreach ((Workspace workspace, string role, string? teamName) in await ListGrantedAsync(userId))
        {
            listed.Add(ToResponse(workspace, currentWorkspaceId, role) with { TeamName = teamName });
        }

        return listed.Select(w => w with { IsDefault = w.Id == defaultWorkspaceId }).ToList();
    }

    // Every workspace other owners' grants reach, one business after another,
    // with the role the person holds there (a workspace's own grant, else the
    // all-workspaces one) and the name the business goes by.
    private async Task<List<(Workspace Workspace, string Role, string? TeamName)>> ListGrantedAsync(string userId)
    {
        List<(Workspace, string, string?)> granted = [];
        IReadOnlyList<WorkspaceGrant> grants = await _team.ListGrantsForUserAsync(userId);

        foreach (IGrouping<string, WorkspaceGrant> team in grants.Where(g => g.OwnerId != userId).GroupBy(g => g.OwnerId))
        {
            List<WorkspaceGrant> held = team.ToList();
            IReadOnlyList<Workspace> theirs = await _repository.ListByOwnerAsync(team.Key);
            string? teamName = theirs.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.Name))?.Name;

            foreach (Workspace workspace in theirs)
            {
                // The role as people see it, built-in or the owner's own, as the access check names it.
                if (WorkspaceAccessService.PickGrant(held, workspace.Id) is not null
                    && await _access.GetAsync(userId, workspace.Id) is { } access)
                {
                    granted.Add((workspace, access.Role, teamName));
                }
            }
        }

        return granted;
    }

    public async Task<WorkspaceResponse> CreateAsync(string userId, CreateWorkspaceRequest request)
    {
        Workspace workspace = new()
        {
            OwnerId = userId,
            Name = ToName(request.Name),
            LogoUrl = request.LogoUrl is null ? null : ToOptionalLogoUrl(request.LogoUrl),
        };

        // A fresh Guid cannot collide, so a false here is not a race to retry.
        if (!await _repository.TryAddAsync(workspace))
        {
            throw new ConflictException("The workspace could not be created. Try again.");
        }

        using (_logger.BeginEntityScope("Workspace", workspace.Id))
        {
            _logger.LogInformation("Workspace created for tenant {UserId}", userId);
        }

        await AnnounceAsync(workspace.Id);
        return ToResponse(workspace, currentWorkspaceId: null);
    }

    public async Task<WorkspaceResponse> UpdateAsync(string userId, string workspaceId, UpdateWorkspaceRequest request)
    {
        Workspace workspace = await GetOwnedAsync(userId, workspaceId);

        if (request.Name is not null)
        {
            workspace.Name = ToName(request.Name);
        }

        // An empty string is the logo being removed.
        if (request.LogoUrl is not null)
        {
            workspace.LogoUrl = ToOptionalLogoUrl(request.LogoUrl);
        }

        workspace.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(workspace);
        _access.Forget(workspace.Id, userId);

        using (_logger.BeginEntityScope("Workspace", workspace.Id))
        {
            _logger.LogInformation("Workspace updated by {UserId}", userId);
        }

        return ToResponse(workspace, workspaceId);
    }

    public async Task<Workspace> GetOwnedAsync(string userId, string workspaceId)
    {
        Workspace? workspace = await _repository.GetByIdAsync(workspaceId);
        if (workspace is null || workspace.OwnerId != userId)
        {
            _logger.LogWarning("Workspace {WorkspaceId} not found for {UserId}", workspaceId, userId);
            throw new NotFoundException("Workspace not found.");
        }

        return workspace;
    }

    public async Task<WorkspaceAccess> GetAccessibleAsync(string userId, string workspaceId)
    {
        WorkspaceAccess? access = await _access.GetAsync(userId, workspaceId);
        if (access is null)
        {
            _logger.LogWarning("Workspace {WorkspaceId} not reachable for {UserId}", workspaceId, userId);
            throw new NotFoundException("Workspace not found.");
        }

        return access;
    }

    public async Task<WorkspaceAccess> ResolveForSignInAsync(ApplicationUser user)
    {
        // The chosen default, then the last one; each owned or joined, as long
        // as it can still be reached. A member removed from their default
        // resumes instead, rather than being stopped at the door.
        foreach (string? preferred in new[] { user.DefaultWorkspaceId, user.LastWorkspaceId })
        {
            if (preferred is not null && await _access.GetAsync(user.Id, preferred) is { } reachable)
            {
                return reachable;
            }
        }

        IReadOnlyList<Workspace> owned = await _repository.ListByOwnerAsync(user.Id);
        if (owned.Count > 0)
        {
            return WorkspaceAccess.ForOwner(owned[0]);
        }

        // Someone who joined a team without ever making a workspace of their own.
        foreach ((Workspace granted, _, _) in await ListGrantedAsync(user.Id))
        {
            if (await _access.GetAsync(user.Id, granted.Id) is { } joined)
            {
                return joined;
            }
        }

        // The first workspace has an id of its own, so the account (the owner)
        // and the business are never the same id. It is derived from the user id
        // rather than random so two sign-ins racing to make it pick the same id
        // and only one row is saved. Accounts from before this keep the first
        // workspace they already have, whose id is their user id.
        Workspace first = new()
        {
            Id = FirstWorkspaceId(user.Id),
            OwnerId = user.Id,
            Name = user.BusinessName,
            LogoUrl = user.LogoUrl,
        };

        if (await _repository.TryAddAsync(first))
        {
            using (_logger.BeginEntityScope("Workspace", first.Id))
            {
                _logger.LogInformation("First workspace created for tenant {UserId}", user.Id);
            }

            await AnnounceAsync(first.Id);
            return WorkspaceAccess.ForOwner(first);
        }

        // Another sign-in made it a moment ago.
        Workspace made = await _repository.GetByIdAsync(first.Id)
            ?? throw new InvalidOperationException($"Workspace {first.Id} was reported as existing but could not be read.");
        return WorkspaceAccess.ForOwner(made);
    }

    // Best-effort: the workspace is made either way, and it is announced again (with its owner)
    // in the snapshot its first channel sends.
    private async Task AnnounceAsync(string workspaceId)
    {
        try
        {
            await _sources.PublishWorkspaceCreatedAsync(workspaceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "workspace.created.snapshot_failed WorkspaceId={WorkspaceId}; continuing", workspaceId);
        }
    }

    /// <summary>
    /// The id of a user's first workspace: a name-based UUID (version 5) of the
    /// user id, so it is the same on every call yet never equal to the user id.
    /// </summary>
    public static string FirstWorkspaceId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        byte[] name = System.Text.Encoding.UTF8.GetBytes(userId);
        byte[] input = new byte[FirstWorkspaceNamespace.Length + name.Length];
        FirstWorkspaceNamespace.CopyTo(input, 0);
        name.CopyTo(input, FirstWorkspaceNamespace.Length);

        byte[] hash = System.Security.Cryptography.SHA1.HashData(input);
        byte[] bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    // Fixed for good: changing it would give existing users a second first workspace.
    private static readonly byte[] FirstWorkspaceNamespace = new Guid("6f1b8a52-3c4e-4d0b-9a77-2f5d1c9e8b40").ToByteArray(bigEndian: true);

    internal static WorkspaceResponse ToResponse(Workspace workspace, string? currentWorkspaceId, string role = WorkspaceRoles.Owner) =>
        new(workspace.Id, workspace.Name, workspace.LogoUrl, workspace.Id == currentWorkspaceId, workspace.CreatedAt, role, workspace.OwnerId);

    private static string ToName(string value)
    {
        string name = value.Trim();
        if (name.Length is < 1 or > 256)
        {
            throw new ValidationException(AuthMessages.BusinessNameTooLong);
        }

        return name;
    }

    private static string? ToOptionalLogoUrl(string value)
    {
        string url = value.Trim();
        if (url.Length == 0)
        {
            return null;
        }

        bool isWebUrl = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return isWebUrl ? url : throw new ValidationException(AuthMessages.InvalidLogoUrl);
    }
}
