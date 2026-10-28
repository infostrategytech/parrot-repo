using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.Auth;
using Parrot.Application.Tasks;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// Where an agent raises a task from a conversation it is answering: an
/// order to confirm, a quote to work out, a customer who wants a person.
/// Only a service token opens it; the tenant is read from the token alone,
/// and the agent and the conversation must both be that workspace's.
/// </summary>
[ApiController]
[Route("api/internal/tasks")]
[Authorize(Policy = ServiceScopes.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "internal.tasks")]
public sealed class InternalTasksController(TaskService tasks) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Raise([FromBody] RaiseTaskRequest request, CancellationToken cancellationToken)
    {
        TaskResponse response = await tasks.RaiseAsync(Tenant(), request, cancellationToken);
        return Created($"api/tasks/{response.Number}", response);
    }

    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
