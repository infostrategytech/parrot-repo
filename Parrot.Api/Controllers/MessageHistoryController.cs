using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Messaging;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// Recovers what live delivery missed: a conversation's stored messages after
/// a sequence, from MongoDB. A client calls it after a reconnect, or whenever
/// it suspects a gap, and merges the result with its pushes by message id.
///
/// The conversation key travels in the query string, not the path: it is two
/// phone numbers, and request logs print the path on every line.
/// </summary>
[ApiController]
[Authorize]
[Route("api/conversations/history")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "conversations")]
[RequirePermission(WorkspacePermissions.ConversationsView)]
public sealed class MessageHistoryController(IMessageHistory history) : ControllerBase
{
    public const int DefaultPageSize = 50;

    // The push's names and platform-as-text, but the default (HTML-safe)
    // escaping: MessageJson relaxes it for the bus, and this goes to browsers.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // From the token, never the request: only the session's workspace's messages are read.
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    /// <summary>The messages after <paramref name="after"/>, oldest first, with the cursor for the next page.</summary>
    /// <remarks>
    /// Numbers are taken before a message is written, so for a moment a later
    /// number can be stored before an earlier one. A client recovering a gap
    /// should ask from a little below the highest sequence it has, not from
    /// it exactly, and drop what it already has by message id.
    /// </remarks>
    [Monitor("conversations.history")]
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? key, [FromQuery] long after = 0,
        [FromQuery] int limit = DefaultPageSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || after < 0)
            throw new ValidationException("Supply a conversation key and a sequence of 0 or more to read after.");

        MessageHistoryPage page = ConversationKey.PlatformOf(key) is { } platform && HttpContext.WorkspaceAccess().CanReach(platform)
            ? await history.GetAfterAsync(Owner, key, after, limit, cancellationToken)
            : new MessageHistoryPage { Messages = [] };

        // The live push's wire format (platform as text, camelCase), so the
        // client reads a recovered message and a pushed one the same way.
        return new JsonResult(page, JsonOptions);
    }
}
