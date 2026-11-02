using Test.Parrot.Api.Tests.Services.Teams;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Agents;
using Parrot.Application.Messaging;
using Test.Parrot.Api.Tests.Services.Realtime;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class AgentEventsRouterTests
{
    private const string Owner = "owner-1";

    private readonly FakeAgentActivityStore _activity = new();
    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private IReadOnlyList<string>? _addressed;

    public AgentEventsRouterTests()
    {
        _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(ids => _addressed = ids)
            .Returns(_proxy.Object);
    }

    private AgentEventsRouter Router()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new AgentEventsRouter(_activity, _sessions, hub.Object, new PassThroughAudience(), TimeProvider.System, NullLogger<AgentEventsRouter>.Instance);
    }

    // The orchestrator's JSON, read exactly as the bus reads it.
    private static AgentEventEnvelope Event(string type = AgentActivityTypes.TurnCompleted, string turn = "turn_1", string status = "completed", string owner = Owner)
    {
        string payload = type switch
        {
            AgentActivityTypes.TurnCompleted =>
                $$"""{"status":"{{status}}","decision":"PERMIT","confidence":0.91,"reply":"Yes, we deliver to Lekki.","delivered":true,"channel":"whatsapp","customer_ref":"2348012345678","customer_name":"Ada","route":["catalog"],"escalation_id":null,"approval_id":null,"cost_usd":0.0012,"latency_ms":840.5,"message_id":"whatsapp:wamid.1","agent_id":"agent-1","intent":"delivery"}""",
            AgentActivityTypes.ApprovalRequested =>
                """{"approval_id":"apr_1","tool":"payments.refund","args":{"amount_minor":500000},"reason":"Refund above the limit","role":"manager","timeout_minutes":30,"customer_ref":"2348012345678"}""",
            _ =>
                """{"escalation_id":"esc_1","priority":"P2","problem":"No refund policy for exchanges","customer_ref":"2348012345678","channel":"whatsapp","external_ref":null,"approval_id":null,"confidence":0.3}""",
        };
        string json = $$"""{"type":"{{type}}","tenant_id":"{{owner}}","conversation_key":"whatsapp:2348012345678:2349011112222","turn_id":"{{turn}}","occurred_at":"2026-10-08T12:00:00.123456+00:00","correlation_id":"4bf92f3577b34da6a3ce929d0e0e4736","schema_version":1,"payload":{{payload}}}""";
        return JsonSerializer.Deserialize<AgentEventEnvelope>(json, MessageJson.Options)!;
    }

    private void VerifyPushed(Times times) => _proxy.Verify(p => p.SendCoreAsync(
        MessagingHub.AgentActivityMethod,
        It.Is<object?[]>(args => args.Length == 1 && args[0] is AgentActivityEvent),
        It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task RouteAsync_FirstDelivery_StoresTheEventAndPushesItToTheOwner()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        await _sessions.RegisterAsync(Owner, "phone");

        await Router().RouteAsync(Event(), CancellationToken.None);

        AgentActivityRecord stored = Assert.Single(_activity.Records);
        Assert.Equal(Owner, stored.OwnerUserId);
        Assert.Equal("turn_1:completed", stored.EventKey);
        Assert.Equal("completed", stored.Status);
        Assert.Equal("PERMIT", stored.Decision);
        Assert.Equal(0.91, stored.Confidence);
        Assert.Equal("Ada", stored.CustomerName);
        Assert.Equal("agent-1", stored.AgentId);
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560), stored.OccurredAt);
        Assert.Contains("\"intent\":\"delivery\"", stored.Payload);
        VerifyPushed(Times.Once());
        Assert.Equal(["laptop", "phone"], _addressed);
    }

    [Fact]
    public async Task RouteAsync_Redelivery_StoresOnceAndPushesOnce()
    {
        await _sessions.RegisterAsync(Owner, "laptop");

        await Router().RouteAsync(Event(), CancellationToken.None);
        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Single(_activity.Records);
        Assert.Equal(1, _activity.Inserts);
        VerifyPushed(Times.Once());
    }

    // A turn that waited for an approval reports twice; both reports are kept.
    [Fact]
    public async Task RouteAsync_TheSameTurnWithAnotherStatus_IsANewEvent()
    {
        await Router().RouteAsync(Event(status: "awaiting_approval"), CancellationToken.None);
        await Router().RouteAsync(Event(status: "completed"), CancellationToken.None);

        Assert.Equal(2, _activity.Records.Count);
    }

    [Fact]
    public async Task RouteAsync_OfflineOwner_StoresWithoutPushing()
    {
        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Single(_activity.Records);
        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task RouteAsync_OnlyTheOwnersConnectionsAreAddressed()
    {
        await _sessions.RegisterAsync(Owner, "owner-tab");
        await _sessions.RegisterAsync("another-business", "their-tab");

        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(["owner-tab"], _addressed);
    }

    [Fact]
    public async Task RouteAsync_AnApprovalRequest_IsKeyedOnTheApproval()
    {
        await Router().RouteAsync(Event(type: AgentActivityTypes.ApprovalRequested), CancellationToken.None);

        AgentActivityRecord stored = Assert.Single(_activity.Records);
        Assert.Equal("apr_1", stored.EventKey);
        Assert.Equal("apr_1", stored.ApprovalId);
        Assert.Equal("payments.refund", stored.Tool);
        Assert.Equal("Refund above the limit", stored.Reason);
        Assert.Null(stored.ResolvedAt);
    }

    [Fact]
    public async Task RouteAsync_AnEscalation_IsKeyedOnTheEscalation()
    {
        await Router().RouteAsync(Event(type: AgentActivityTypes.EscalationOpened), CancellationToken.None);

        AgentActivityRecord stored = Assert.Single(_activity.Records);
        Assert.Equal("esc_1", stored.EventKey);
        Assert.Equal("P2", stored.Priority);
        Assert.Equal("No refund policy for exchanges", stored.Problem);
    }

    [Fact]
    public async Task RouteAsync_TheApisOwnConfigEvent_IsSkipped()
    {
        AgentEventEnvelope envelope = JsonSerializer.Deserialize<AgentEventEnvelope>(
            """{"type":"agent.config.changed","tenant_id":"owner-1","occurred_at":"2026-10-08T12:00:00+00:00","payload":{"agent_id":"a","version":2,"channels":[],"is_live":true}}""",
            MessageJson.Options)!;

        await Router().RouteAsync(envelope, CancellationToken.None);

        Assert.Empty(_activity.Records);
        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task RouteAsync_AnEventWithNoTenant_IsSkippedNotRetried()
    {
        AgentEventEnvelope envelope = JsonSerializer.Deserialize<AgentEventEnvelope>(
            """{"type":"agent.turn.completed","turn_id":"turn_9","occurred_at":"2026-10-08T12:00:00+00:00","payload":{"status":"completed"}}""",
            MessageJson.Options)!;

        await Router().RouteAsync(envelope, CancellationToken.None);

        Assert.Empty(_activity.Records);
    }

    // The store is the record, so the bus must redeliver when it is down.
    [Fact]
    public async Task RouteAsync_StoreDown_ThrowsSoTheBusRedelivers()
    {
        _activity.Failure = new TimeoutException("MongoDB is down");

        await Assert.ThrowsAsync<TimeoutException>(() => Router().RouteAsync(Event(), CancellationToken.None));
    }

    [Fact]
    public async Task RouteAsync_RedisDown_StoresAndDoesNotThrow()
    {
        _sessions.Failure = new StackExchange.Redis.RedisConnectionException(StackExchange.Redis.ConnectionFailureType.UnableToConnect, "Redis is down");

        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Single(_activity.Records);
        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task RouteAsync_SignalRFailure_DoesNotThrow()
    {
        await _sessions.RegisterAsync(Owner, "conn-1");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("connection reset"));

        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Single(_activity.Records);
    }

    [Fact]
    public async Task RouteAsync_Cancellation_IsNotSwallowed()
    {
        await _sessions.RegisterAsync(Owner, "conn-1");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Router().RouteAsync(Event(), CancellationToken.None));
    }

    [Fact]
    public void TryMap_WithAnUnreadableInstant_UsesTheTimeItArrived()
    {
        AgentEventEnvelope envelope = Event();
        envelope.OccurredAt = "yesterday-ish";
        DateTime received = new(2026, 10, 8, 9, 30, 0, DateTimeKind.Utc);

        bool mapped = AgentActivityMapper.TryMap(envelope, received, out AgentActivityRecord? record, out _);

        Assert.True(mapped);
        Assert.Equal(received, record!.OccurredAt);
    }

    [Fact]
    public void TryMap_WithAnOversizedPayload_StoresEmptyObjectInstead()
    {
        string bigJson = "{\"data\":\"" + new string('x', AgentActivityMapper.MaxPayloadBytes + 1) + "\"}";
        AgentEventEnvelope envelope = new()
        {
            Type = AgentActivityTypes.TurnCompleted,
            TenantId = Owner,
            TurnId = "turn_1",
            OccurredAt = "2026-10-08T12:00:00+00:00",
            Payload = JsonSerializer.Deserialize<JsonElement>(bigJson),
        };

        bool mapped = AgentActivityMapper.TryMap(envelope, DateTime.UtcNow, out AgentActivityRecord? record, out _);

        Assert.True(mapped);
        Assert.Equal("{}", record!.Payload);
    }
}
