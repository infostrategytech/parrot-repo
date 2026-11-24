using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Teams;
using Parrot.Application.Wallets;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The current workspace's wallet: what its customers have paid it, and every movement in and out.
/// The workspace is always the session's, never one named in the request.
/// </summary>
[ApiController]
[Route("api/payments/wallet")]
[Authorize]
[Monitor(Area = "payments")]
public sealed class WalletController(WalletService wallets) : ControllerBase
{
    private string WorkspaceId => User.WorkspaceId() ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    [EndpointSummary("This workspace's wallet balance")]
    [EndpointDescription("In naira: balance (what the business has now), totalIn (every credit) and totalOut (every debit), and when it last moved (updatedAt).")]
    [ProducesResponseType(typeof(WalletResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<WalletResponse>> Get(CancellationToken ct) =>
        Ok(await wallets.GetAsync(WorkspaceId, ct));

    [HttpGet("transactions")]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    [EndpointSummary("This workspace's wallet ledger")]
    [EndpointDescription("Every movement in and out, newest first. Each has a type (Credit: money in; Debit: money out), a status (Success, or Failed: on record but the balance didn't move), a kind (Payment: a customer paid an order; Fee, Payout or Adjustment), who made it happen (actionBy: a user id, or a system actor such as system:accelerate; byName in words) and the balance just after it. Filter with type and status. page starts at 1; pageSize up to 100 (default 20).")]
    [ProducesResponseType(typeof(WalletTransactionsPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WalletTransactionsPage>> Transactions(
        [FromQuery] WalletTransactionType? type, [FromQuery] WalletTransactionStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = WalletService.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await wallets.ListAsync(WorkspaceId, new WalletTransactionFilter(type, status), page, pageSize, ct));
}
