using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class ConnectionServiceTests
{
    private const string WorkspaceId = "ws-1";
    private const string OwnerId = "user-1";
    private const string OtherWorkspaceId = "ws-2";
    private const string OtherOwnerId = "user-2";

    private readonly Mock<IConnectionRepository> _connections = new();
    private readonly Mock<IWorkspaceRepository> _workspaces = new();
    private readonly ConnectionService _sut;

    public ConnectionServiceTests()
    {
        _sut = new ConnectionService(_connections.Object, _workspaces.Object);

        _workspaces.Setup(w => w.GetByIdAsync(WorkspaceId))
            .ReturnsAsync(new Workspace { Id = WorkspaceId, OwnerId = OwnerId });
    }

    private void ConnectionBelongsToWorkspace(Guid id, string channelKey = "whatsapp") =>
        _connections.Setup(c => c.FindAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo(id, WorkspaceId, OwnerId, channelKey, IsDisabled: false));

    private void ConnectionBelongsToSameTenantDifferentWorkspace(Guid id) =>
        _connections.Setup(c => c.FindAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo(id, OtherWorkspaceId, OwnerId, "whatsapp", IsDisabled: false));

    private void ConnectionBelongsToDifferentTenant(Guid id) =>
        _connections.Setup(c => c.FindAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo(id, OtherWorkspaceId, OtherOwnerId, "whatsapp", IsDisabled: false));

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenConnectionBelongsToWorkspace_ReturnsChannelKey()
    {
        Guid id = Guid.NewGuid();
        ConnectionBelongsToWorkspace(id, "whatsapp");

        ConnectionValidationResult result = await _sut.ValidateForWorkspaceAsync(id, WorkspaceId);

        Assert.Equal(id, result.Id);
        Assert.Equal("whatsapp", result.ChannelKey);
        Assert.Equal(WorkspaceId, result.WorkspaceId);
    }

    [Theory]
    [InlineData("facebook")]
    [InlineData("instagram")]
    [InlineData("tiktok")]
    public async Task ValidateForWorkspaceAsync_ReturnsCorrectChannelKey(string channelKey)
    {
        Guid id = Guid.NewGuid();
        ConnectionBelongsToWorkspace(id, channelKey);

        ConnectionValidationResult result = await _sut.ValidateForWorkspaceAsync(id, WorkspaceId);

        Assert.Equal(channelKey, result.ChannelKey);
    }

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenConnectionNotFound_ThrowsNotFound()
    {
        Guid id = Guid.NewGuid();
        _connections.Setup(c => c.FindAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectionInfo?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ValidateForWorkspaceAsync(id, WorkspaceId));
    }

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenSameTenantDifferentWorkspace_ThrowsForbidden()
    {
        Guid id = Guid.NewGuid();
        ConnectionBelongsToSameTenantDifferentWorkspace(id);

        // The other workspace also belongs to the same owner.
        _workspaces.Setup(w => w.GetByIdAsync(OtherWorkspaceId))
            .ReturnsAsync(new Workspace { Id = OtherWorkspaceId, OwnerId = OwnerId });

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ValidateForWorkspaceAsync(id, WorkspaceId));
    }

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenDifferentTenant_ThrowsNotFound()
    {
        Guid id = Guid.NewGuid();
        ConnectionBelongsToDifferentTenant(id);

        _workspaces.Setup(w => w.GetByIdAsync(OtherWorkspaceId))
            .ReturnsAsync(new Workspace { Id = OtherWorkspaceId, OwnerId = OtherOwnerId });

        // Same response as not found — no existence leak.
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ValidateForWorkspaceAsync(id, WorkspaceId));
    }

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenDifferentTenant_DoesNotRevealItExistsByStatusCode()
    {
        // The NotFoundException message must be the same whether the connection does not exist
        // or belongs to another tenant.
        Guid missingId = Guid.NewGuid();
        Guid foreignId = Guid.NewGuid();
        _connections.Setup(c => c.FindAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectionInfo?)null);
        ConnectionBelongsToDifferentTenant(foreignId);
        _workspaces.Setup(w => w.GetByIdAsync(OtherWorkspaceId))
            .ReturnsAsync(new Workspace { Id = OtherWorkspaceId, OwnerId = OtherOwnerId });

        NotFoundException missingEx = await Assert.ThrowsAsync<NotFoundException>(() => _sut.ValidateForWorkspaceAsync(missingId, WorkspaceId));
        NotFoundException foreignEx = await Assert.ThrowsAsync<NotFoundException>(() => _sut.ValidateForWorkspaceAsync(foreignId, WorkspaceId));

        Assert.Equal(missingEx.Message, foreignEx.Message);
    }

    [Fact]
    public async Task ValidateForWorkspaceAsync_WhenCallerWorkspaceNotFound_ThrowsNotFound()
    {
        Guid id = Guid.NewGuid();
        ConnectionBelongsToSameTenantDifferentWorkspace(id);

        // Caller's workspace has been deleted or is invalid.
        _workspaces.Setup(w => w.GetByIdAsync(WorkspaceId)).ReturnsAsync((Workspace?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ValidateForWorkspaceAsync(id, WorkspaceId));
    }
}
