using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Security;
using Parrot.Application.Customers;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/customers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequirePermission(WorkspacePermissions.ConversationsView)]
public sealed class CustomersController(CustomerService customers) : ControllerBase
{
    // Customers belong to the workspace the session works in.
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    // The connections a channel-limited team member reaches; null for everyone else.
    private IReadOnlySet<Guid>? Reach => HttpContext.WorkspaceAccess().AllowedConnections;

    [HttpGet]
    public async Task<IActionResult> List(
        string? search = null,
        string? stage = null,
        string? channel = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);
        CustomerPageResponse page = await customers.ListAsync(Owner, search, stage, channel, skip, take, cancellationToken, Reach);
        return Ok(page);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.GetAsync(Owner, id, cancellationToken, Reach);
        return Ok(response);
    }

    [HttpPost]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> Create(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.CreateAsync(Owner, request, cancellationToken);
        return Created($"api/customers/{response.Id}", response);
    }

    [HttpPut("{id:guid}/stage")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> UpdateStage(
        Guid id,
        [FromBody] UpdateStageRequest request,
        CancellationToken cancellationToken = default)
    {
        await customers.GetAsync(Owner, id, cancellationToken, Reach);
        CustomerDetailResponse response = await customers.UpdateStageAsync(Owner, id, request.Stage, cancellationToken);
        return Ok(response);
    }

    [HttpPut("{id:guid}/tags")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> UpdateTags(
        Guid id,
        [FromBody] UpdateTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        await customers.GetAsync(Owner, id, cancellationToken, Reach);
        CustomerDetailResponse response = await customers.UpdateTagsAsync(Owner, id, request.Tags, cancellationToken);
        return Ok(response);
    }

    [HttpPut("{id:guid}/channel")]
    public async Task<IActionResult> UpdateChannelIdentifier(
        Guid id,
        [FromBody] UpdateChannelIdentifierRequest request,
        CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.UpdateChannelIdentifierAsync(
            Owner, id, request.ChannelPlatform, request.ChannelIdentifier, cancellationToken);
        return Ok(response);
    }
}
