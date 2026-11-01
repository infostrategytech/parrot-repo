using Parrot.Application.Auth;
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
    private readonly IAppLogger<WorkspaceService> _logger;

    public WorkspaceService(
        IWorkspaceRepository repository,
        ITeamRepository team,
        IWorkspaceAccessService access,
        IAppLogger<WorkspaceService> logger)
    {
        _repository = repository;
        _team = team;
        _access = access;
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

        // The first workspace takes the user's own id: everything the account
        // wrote before workspaces existed is keyed by it, so nothing has to move.
        // The migration made one for every account it found; this covers the
        // accounts made since.
        Workspace first = new()
        {
            Id = user.Id,
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

            return WorkspaceAccess.ForOwner(first);
        }

        // Another sign-in made it a moment ago.
        Workspace made = await _repository.GetByIdAsync(user.Id)
            ?? throw new InvalidOperationException($"Workspace {user.Id} was reported as existing but could not be read.");
        return WorkspaceAccess.ForOwner(made);
    }

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
