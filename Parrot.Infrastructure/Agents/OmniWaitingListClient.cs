using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Calls the AI service's waiting list with the signed-in person's own session token. The service speaks
/// snake_case; the private records below are its wire shapes. Its error bodies are never surfaced: what it
/// said (its errorCode and traceId) is logged, and a client sees one 503 whatever went wrong on the other side.
/// </summary>
public sealed class OmniWaitingListClient(
    HttpClient http,
    IOptions<OmniAiSettings> settings,
    IAppLogger<OmniWaitingListClient> logger) : IWaitingListClient
{
    public const string WaitingPath = "api/v1/ai-operations/waiting";

    /// <summary>The service's largest page.</summary>
    public const int PageSize = 100;

    /// <summary>Pages read at most: 1,000 items, far more than anyone works through, and a bound on one read.</summary>
    public const int MaxPages = 10;

    private const string Unavailable = "The AI service could not be reached. Try again in a moment.";
    private const string NotFound = "Question not found.";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WaitingListItem>> ListOpenAsync(string userToken, CancellationToken cancellationToken = default)
    {
        List<WaitingListItem> items = [];
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string path = $"{WaitingPath}?limit={PageSize}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            PageWire wire = await SendAsync<PageWire>(HttpMethod.Get, path, userToken, cancellationToken);
            items.AddRange(wire.Items.Select(ToItem));
            cursor = wire.NextCursor;
            if (cursor is null) return items;
        }

        logger.LogWarning("waiting_list.capped Pages={Pages} Items={Items}: later items are left out", MaxPages, items.Count);
        return items;
    }

    public async Task<WaitingListItem> GetAsync(string userToken, string itemId, CancellationToken cancellationToken = default) =>
        ToItem(await SendAsync<ItemWire>(HttpMethod.Get, $"{WaitingPath}/{Uri.EscapeDataString(itemId)}", userToken, cancellationToken));

    public async Task<WaitingListItem> ResolveAsync(string userToken, string itemId, CancellationToken cancellationToken = default) =>
        ToItem(await SendAsync<ItemWire>(HttpMethod.Post, $"{WaitingPath}/{Uri.EscapeDataString(itemId)}/resolve", userToken, cancellationToken));

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string userToken, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled || http.BaseAddress is null)
        {
            throw new ServiceUnavailableException(
                "The AI service is not enabled on this deployment.",
                new InvalidOperationException("OmniAi:Enabled is false or OmniAi:BaseUrl is not an absolute URL."));
        }

        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "waiting_list.unreachable Method={Method} Path={Path}", method, WaitingPath);
            throw new ServiceUnavailableException(Unavailable, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ErrorWire? error = await ReadErrorAsync(response, cancellationToken);

                // No such item in the token's workspace: the same answer for another workspace's.
                if (response.StatusCode == HttpStatusCode.NotFound && error?.ErrorCode == "WAITING_ITEM_NOT_FOUND")
                    throw new NotFoundException(NotFound);

                logger.LogWarning(
                    "waiting_list.failed Method={Method} Path={Path} Status={Status} ErrorCode={ErrorCode} TraceId={TraceId}",
                    method, WaitingPath, (int)response.StatusCode, error?.ErrorCode, error?.TraceId);
                throw new ServiceUnavailableException(Unavailable, new HttpRequestException(
                    $"The waiting list answered {(int)response.StatusCode} {error?.ErrorCode}.", null, response.StatusCode));
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Wire, cancellationToken)
                    ?? throw new ServiceUnavailableException(Unavailable, new InvalidOperationException("The waiting list answered with an empty body."));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "waiting_list.unreadable Method={Method} Path={Path}", method, WaitingPath);
                throw new ServiceUnavailableException(Unavailable, ex);
            }
        }
    }

    // The service's error envelope, when there is one: only its code and trace id are kept, for the log.
    private static async Task<ErrorWire?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorWire>(Wire, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static WaitingListItem ToItem(ItemWire wire) => new()
    {
        Id = wire.Id,
        Status = wire.Status,
        Kind = wire.Kind,
        AgentId = wire.AgentId,
        CustomerRef = wire.CustomerRef,
        CustomerName = wire.CustomerName,
        ConversationKey = wire.ConversationKey,
        Summary = wire.Summary ?? string.Empty,
        Priority = wire.Priority,
        QuestionCount = wire.QuestionCount,
        EscalationId = wire.EscalationId,
        OpenedAt = wire.OpenedAt,
        LastAskedAt = wire.LastAskedAt ?? wire.OpenedAt,
        Approval = wire.Approval is { } approval ? new WaitingListApproval(approval.Id, approval.Tool, approval.Reason, approval.Status) : null,
        Resolution = wire.Resolution,
        ResolvedBy = wire.ResolvedBy,
    };

    private sealed record PageWire(
        [property: JsonPropertyName("items")] List<ItemWire> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private sealed record ItemWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("agent_id")] string? AgentId,
        [property: JsonPropertyName("customer_ref")] string? CustomerRef,
        [property: JsonPropertyName("customer_name")] string? CustomerName,
        [property: JsonPropertyName("conversation_key")] string? ConversationKey,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("priority")] string? Priority,
        [property: JsonPropertyName("question_count")] int QuestionCount,
        [property: JsonPropertyName("escalation_id")] string? EscalationId,
        [property: JsonPropertyName("opened_at")] DateTime OpenedAt,
        [property: JsonPropertyName("last_asked_at")] DateTime? LastAskedAt,
        [property: JsonPropertyName("approval")] ApprovalWire? Approval,
        [property: JsonPropertyName("resolution")] string? Resolution,
        [property: JsonPropertyName("resolved_by")] string? ResolvedBy);

    private sealed record ApprovalWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("tool")] string? Tool,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("status")] string Status);

    private sealed record ErrorWire(
        [property: JsonPropertyName("errorCode")] string? ErrorCode,
        [property: JsonPropertyName("traceId")] string? TraceId);
}
