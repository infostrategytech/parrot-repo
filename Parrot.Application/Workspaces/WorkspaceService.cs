using Parrot.Application.Auth;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Workspaces;

public class WorkspaceService : IWorkspaceService
{
    private readonly IWorkspaceRepository _repository;
    private readonly IAppLogger<WorkspaceService> _logger;

    public WorkspaceService(IWorkspaceRepository repository, IAppLogger<WorkspaceService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkspaceResponse>> ListAsync(string userId, string currentWorkspaceId)
    {
        IReadOnlyList<Workspace> owned = await _repository.ListByOwnerAsync(userId);
        return owned.Select(w => ToResponse(w, currentWorkspaceId)).ToList();
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

    public async Task<Workspace> ResolveForSignInAsync(ApplicationUser user)
    {
        IReadOnlyList<Workspace> owned = await _repository.ListByOwnerAsync(user.Id);

        Workspace? last = owned.FirstOrDefault(w => w.Id == user.LastWorkspaceId);
        if (last is not null)
        {
            return last;
        }

        if (owned.Count > 0)
        {
            return owned[0];
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

            return first;
        }

        // Another sign-in made it a moment ago.
        return await _repository.GetByIdAsync(user.Id)
            ?? throw new InvalidOperationException($"Workspace {user.Id} was reported as existing but could not be read.");
    }

    internal static WorkspaceResponse ToResponse(Workspace workspace, string? currentWorkspaceId) =>
        new(workspace.Id, workspace.Name, workspace.LogoUrl, workspace.Id == currentWorkspaceId, workspace.CreatedAt);

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
