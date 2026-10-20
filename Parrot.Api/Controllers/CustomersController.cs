using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.Customers;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/customers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomersController(CustomerService customers) : ControllerBase
{
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedException("Sign in to continue.");

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
        CustomerPageResponse page = await customers.ListAsync(Owner, search, stage, channel, skip, take, cancellationToken);
        return Ok(page);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.GetAsync(Owner, id, cancellationToken);
        return Ok(response);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.CreateAsync(Owner, request, cancellationToken);
        return Created($"api/customers/{response.Id}", response);
    }

    [HttpPut("{id:guid}/stage")]
    public async Task<IActionResult> UpdateStage(
        Guid id,
        [FromBody] UpdateStageRequest request,
        CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.UpdateStageAsync(Owner, id, request.Stage, cancellationToken);
        return Ok(response);
    }

    [HttpPut("{id:guid}/tags")]
    public async Task<IActionResult> UpdateTags(
        Guid id,
        [FromBody] UpdateTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        CustomerDetailResponse response = await customers.UpdateTagsAsync(Owner, id, request.Tags, cancellationToken);
        return Ok(response);
    }
}
