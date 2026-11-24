using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Application.Agents;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Controllers;

// A decision is the signed-in person's: the AI service records them as who decided, and a teammate limited to some
// channels can only decide what was asked on those.
public sealed class AiOperationsControllerTests
{
    private const string Owner = "workspace-1";
    private const string Token = "session-token-1";
    private static readonly Guid WhatsApp = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<IAiOperationsService> _operations = new();

    private AiOperationsController Controller(WorkspaceAccess access)
    {
        AiOperationsController controller = new(_operations.Object);
        DefaultHttpContext http = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(WorkspaceClaims.WorkspaceId, Owner), new Claim(ClaimTypes.NameIdentifier, "user-ngozi")], "test")),
        };
        http.Request.Headers.Authorization = $"Bearer {Token}";
        http.Items[typeof(WorkspaceAccess)] = access;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task Decide_DecidesAsTheSignedInPersonWithTheirTokenAndTheirChannels()
    {
        HashSet<Guid> reach = [WhatsApp];
        WorkspaceAccess teammate = new(new Workspace { Id = Owner, OwnerId = "user-0" }, "Member",
            new HashSet<string> { WorkspacePermissions.ConversationsTakeOver }, new HashSet<string>(), reach);
        _operations.Setup(o => o.DecideApprovalAsync(Owner, Token, It.IsAny<IReadOnlySet<Guid>?>(), "user-ngozi", "apr_1",
                It.IsAny<ApprovalDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalDecisionResponse("apr_1", "approved", "t1", "completed", null));

        IActionResult result = await Controller(teammate).Decide(" apr_1 ", new ApprovalDecisionRequest("approved", null), default);

        Assert.IsType<OkObjectResult>(result);
        _operations.Verify(o => o.DecideApprovalAsync(Owner, Token, It.Is<IReadOnlySet<Guid>?>(r => r != null && r.SetEquals(reach)), "user-ngozi",
            "apr_1", It.IsAny<ApprovalDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
