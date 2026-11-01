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
        int take = 15,
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

    // ── Profile links ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the linked-profile view for <paramref name="id"/>. If the customer is itself a linked
    /// (non-primary) profile, the response is the primary's view — so the caller always gets the
    /// root of the link tree.
    /// </summary>
    [HttpGet("{id:guid}/links")]
    public async Task<IActionResult> GetLinks(Guid id, CancellationToken cancellationToken = default)
    {
        LinkedCustomerProfileResponse response = await customers.GetLinksAsync(Owner, id, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Links the customer at <paramref name="id"/> (the one that will be hidden) under the customer
    /// whose id is in the request body (the primary that keeps its name).
    /// </summary>
    [HttpPost("{id:guid}/links")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> Link(
        Guid id,
        [FromBody] LinkCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        LinkedCustomerProfileResponse response = await customers.LinkAsync(Owner, request.CustomerId, id, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Separates the link identified by <paramref name="linkId"/> from the primary customer at
    /// <paramref name="id"/>. The previously linked customer reappears independently in the list.
    /// </summary>
    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> Unlink(
        Guid id,
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        LinkedCustomerProfileResponse response = await customers.UnlinkAsync(Owner, id, linkId, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Resolves a conversation identity (connectionId + sender) to a linked-profile view. Used by
    /// the Inbox to show the customer profile panel for an open conversation. Returns 200 with a
    /// null Profile when the identity is not yet associated with a customer.
    /// </summary>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve(
        [FromQuery] Guid connectionId,
        [FromQuery] string sender,
        CancellationToken cancellationToken = default)
    {
        CustomerLinkLookupResponse response = await customers.FindByIdentityAsync(Owner, connectionId, sender, cancellationToken);
        return Ok(response);
    }
}
