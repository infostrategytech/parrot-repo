using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Security;
using Parrot.Application.Tasks;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The workspace's tasks. Reading them follows the dashboard's Tasks rule
/// (AccessRules.Tasks): whoever works conversations, and the Analyst's task
/// reports. Working them is for whoever works conversations. A task is
/// addressed by its number in the workspace.
/// </summary>
[ApiController]
[Authorize]
[Route("api/tasks")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver, WorkspacePermissions.AnalyticsTasks)]
public sealed class TasksController(TaskService tasks) : ControllerBase
{
    // Tasks belong to the workspace the session works in.
    private string Workspace => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string Me => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    // The connections a channel-limited team member reaches; null for everyone else.
    private IReadOnlySet<Guid>? Reach => HttpContext.WorkspaceAccess().AllowedConnections;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok(await tasks.ListAsync(Workspace, Reach, cancellationToken));

    /// <summary>Who tasks can be given to, and the person asking.</summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team(CancellationToken cancellationToken = default) =>
        Ok(await tasks.TeamAsync(Workspace, Me, cancellationToken));

    [HttpPost]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Create([FromBody] SaveTaskRequest request, CancellationToken cancellationToken = default)
    {
        TaskResponse response = await tasks.CreateAsync(Workspace, Me, Reach, request, cancellationToken);
        return Created($"api/tasks/{response.Number}", response);
    }

    [HttpPut("{number:int}")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Edit(int number, [FromBody] SaveTaskRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.EditAsync(Workspace, Me, Reach, number, request, cancellationToken));

    [HttpPut("{number:int}/state")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> SetState(int number, [FromBody] SetTaskStateRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.SetStateAsync(Workspace, Me, Reach, number, request.State, cancellationToken));

    [HttpPut("{number:int}/urgent")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> SetUrgent(int number, [FromBody] SetTaskUrgentRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.SetUrgentAsync(Workspace, Me, Reach, number, request.Urgent, cancellationToken));

    [HttpPut("{number:int}/due")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> SetDue(int number, [FromBody] SetTaskDueRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.SetDueAsync(Workspace, Me, Reach, number, request.DueAt, request.HasTime, cancellationToken));

    [HttpPut("{number:int}/assignee")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Assign(int number, [FromBody] AssignTaskRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.AssignAsync(Workspace, Me, Reach, number, request.AssigneeId, cancellationToken));

    /// <summary>An internal note for the team, kept in the task's history.</summary>
    [HttpPost("{number:int}/notes")]
    [RequireAnyPermission(WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> PostNote(int number, [FromBody] PostTaskNoteRequest request, CancellationToken cancellationToken = default) =>
        Ok(await tasks.PostNoteAsync(Workspace, Me, Reach, number, request.Text, cancellationToken));
}
