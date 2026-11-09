using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Calls the orchestrator's agent API with a service token minted for the
/// owner. The orchestrator speaks snake_case; the private records below are
/// its wire shapes, and the public results are this API's.
///
/// Its error bodies are logged at debug level and never surfaced: a client
/// sees a 503 with the same wording whatever went wrong on the other side.
/// </summary>
public sealed class AgentOrchestratorClient(
    HttpClient http,
    IServiceTokenFactory tokens,
    IOptions<OmniAiSettings> settings,
    ILogger<AgentOrchestratorClient> logger) : IAgentOrchestratorClient
{
    public const string ApprovalsPath = "api/v1/agent/approvals/";
    public const string TracesPath = "api/v1/agent/traces/";
    public const string RegistryPath = "api/v1/agent/registry";
    public const string TurnPath = "api/v1/agent/turn";

    private const string Unavailable = "The AI service could not be reached. Try again in a moment.";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ApprovalDecisionResult> DecideApprovalAsync(
        string ownerUserId, string approvalId, ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        ApprovalDecisionWire body = new(decision.Decision, decision.DecidedBy, decision.Instruction);
        ApprovalDecisionResultWire result = await SendAsync<ApprovalDecisionResultWire>(
            HttpMethod.Post, ApprovalsPath + Uri.EscapeDataString(approvalId), ownerUserId, body, cancellationToken);
        return new ApprovalDecisionResult(result.TurnId, result.Status, result.Reply);
    }

    public Task<JsonElement> GetTraceAsync(string ownerUserId, string traceId, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, TracesPath + Uri.EscapeDataString(traceId), ownerUserId, null, cancellationToken);

    public Task<JsonElement> GetRegistryAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, RegistryPath, ownerUserId, null, cancellationToken);

    public async Task<TestTurnResult> RunTestTurnAsync(string ownerUserId, TestTurnRequest request, CancellationToken cancellationToken = default)
    {
        // The orchestrator's ChannelMessage, on the "web" channel so no deliverer sends it anywhere.
        ChannelMessageWire body = new(
            $"web:{Guid.NewGuid():N}", ownerUserId, request.ConversationKey, "web", request.CustomerRef, request.Text, request.CustomerName);
        TurnResultWire result = await SendAsync<TurnResultWire>(HttpMethod.Post, TurnPath, ownerUserId, body, cancellationToken);
        return new TestTurnResult(result.TurnId, result.Status, result.Decision, result.Reply, result.Confidence ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, ConversationAgentRef>> GetChannelAgentMapAsync(
        string ownerUserId, CancellationToken cancellationToken = default)
    {
        if (!settings.Value.Enabled || http.BaseAddress is null)
            return new Dictionary<string, ConversationAgentRef>();

        // A workspace has a handful of agents and channels; limit=100 is effectively all.
        AgentListWire agentList = await SendAsync<AgentListWire>(
            HttpMethod.Get, "api/v1/agents?limit=100", ownerUserId, null, cancellationToken);
        ChannelListWire channelList = await SendAsync<ChannelListWire>(
            HttpMethod.Get, "api/v1/channels?limit=100", ownerUserId, null, cancellationToken);

        Dictionary<string, AgentItemWire> liveById = agentList.Items
            .Where(a => a.IsLive)
            .ToDictionary(a => a.AgentId);

        Dictionary<string, ConversationAgentRef> map = [];
        foreach (ChannelItemWire channel in channelList.Items.Where(c => c.Enabled))
        {
            if (liveById.TryGetValue(channel.AgentId, out AgentItemWire? agent))
                map[channel.Platform.ToLowerInvariant()] = new ConversationAgentRef(agent.AgentId, agent.Name);
        }
        return map;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string ownerUserId, object? body, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled || http.BaseAddress is null)
        {
            throw new ServiceUnavailableException(
                "The AI service is not enabled on this deployment.",
                new InvalidOperationException("OmniAi:Enabled is false or OmniAi:BaseUrl is not an absolute URL."));
        }

        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.MintForAiService(ownerUserId));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Wire);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "orchestrator.unreachable Method={Method} Path={Path}", method, path);
            throw new ServiceUnavailableException(Unavailable, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new NotFoundException("The AI service has no record of that.");

            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new ConflictException("That has already been decided.");

            if (!response.IsSuccessStatusCode)
            {
                // The body may say anything; it is for the log, never for the client.
                string detail = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "orchestrator.failed Method={Method} Path={Path} Status={Status} BodyLength={BodyLength}",
                    method,
                    path,
                    (int)response.StatusCode,
                    detail.Length);
                logger.LogDebug("orchestrator.failed_body Path={Path} Body={Body}", path, detail);
                HttpRequestException status = new($"Orchestrator answered {(int)response.StatusCode}.", null, response.StatusCode);
                throw new ServiceUnavailableException(Unavailable, status);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Wire, cancellationToken)
                    ?? throw new ServiceUnavailableException(Unavailable, new InvalidOperationException("The orchestrator answered with an empty body."));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "orchestrator.unreadable Method={Method} Path={Path}", method, path);
                throw new ServiceUnavailableException(Unavailable, ex);
            }
        }
    }

    private sealed record ApprovalDecisionWire(
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("decided_by")] string DecidedBy,
        [property: JsonPropertyName("instruction")] string? Instruction);

    private sealed record ApprovalDecisionResultWire(
        [property: JsonPropertyName("turn_id")] string? TurnId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("reply")] string? Reply);

    private sealed record ChannelMessageWire(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("tenant_id")] string TenantId,
        [property: JsonPropertyName("conversation_key")] string ConversationKey,
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("customer_ref")] string CustomerRef,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("customer_name")] string? CustomerName);

    private sealed record TurnResultWire(
        [property: JsonPropertyName("turn_id")] string? TurnId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("decision")] string? Decision,
        [property: JsonPropertyName("reply")] string? Reply,
        [property: JsonPropertyName("confidence")] double? Confidence);

    private sealed record AgentListWire(
        [property: JsonPropertyName("items")] List<AgentItemWire> Items);

    private sealed record AgentItemWire(
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("is_live")] bool IsLive);

    private sealed record ChannelListWire(
        [property: JsonPropertyName("items")] List<ChannelItemWire> Items);

    private sealed record ChannelItemWire(
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("enabled")] bool Enabled);
}
