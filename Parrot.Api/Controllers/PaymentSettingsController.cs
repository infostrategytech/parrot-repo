using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Payments;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The current workspace's checkout settings. The workspace is always the session's, never one
/// named in the request, so a business only ever reads or changes its own.
/// </summary>
[ApiController]
[Route("api/payments/settings")]
[Authorize]
[Monitor(Area = "payments")]
public sealed class PaymentSettingsController(WorkspacePaymentSettingsService settings) : ControllerBase
{
    private string WorkspaceId => User.WorkspaceId() ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    [EndpointSummary("This workspace's checkout settings")]
    [EndpointDescription("How long a checkout link stays open, when stock is kept aside (AtPay, AtCheckout or Never), how long the payment link stays open after Pay, and what a price change does (ConfirmNewPrice or Block). isDefault: never saved, so the platform's defaults apply. limits: the ranges allowed.")]
    [ProducesResponseType(typeof(PaymentSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentSettingsResponse>> Get(CancellationToken ct) =>
        Ok(await settings.GetAsync(WorkspaceId, ct));

    [HttpPut]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    [EndpointSummary("Change this workspace's checkout settings")]
    [EndpointDescription("Applies to checkouts made from now on; when stock is kept aside is fixed per checkout when it's made.")]
    [ProducesResponseType(typeof(PaymentSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentSettingsResponse>> Update([FromBody] UpdatePaymentSettingsRequest request, CancellationToken ct) =>
        Ok(await settings.UpdateAsync(WorkspaceId, User.UserId(), request, ct));
}
