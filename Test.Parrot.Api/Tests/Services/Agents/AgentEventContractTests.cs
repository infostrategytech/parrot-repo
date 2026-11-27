using System.Text.Json;
using Parrot.Application.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Test.Parrot.Api.Tests.Services.Agents;

// Both directions of the agent events topic are written with the bus options
// (camelCase) but must read as the orchestrator's snake_case contract.
public class AgentEventContractTests
{
    [Fact]
    public void AgentConfigChangedEvent_IsWrittenInTheOrchestratorsShape()
    {
        AgentConfigChangedEvent changed = new()
        {
            TenantId = "owner-1",
            OccurredAt = new DateTimeOffset(2026, 10, 8, 13, 0, 0, 500, TimeSpan.FromHours(1)),
            CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
            Payload = new AgentConfigChangedPayload { AgentId = "agent-1", Version = 4, Channels = ["whatsapp"], IsLive = true },
        };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(changed, MessageJson.Options));
        JsonElement root = json.RootElement;

        Assert.Equal("agent.config.changed", root.GetProperty("type").GetString());
        Assert.Equal("owner-1", root.GetProperty("tenant_id").GetString());
        Assert.Equal("2026-10-08T12:00:00.500000+00:00", root.GetProperty("occurred_at").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("conversation_key").ValueKind);
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal("agent-1", payload.GetProperty("agent_id").GetString());
        Assert.Equal(4, payload.GetProperty("version").GetInt32());
        Assert.Equal("whatsapp", payload.GetProperty("channels")[0].GetString());
        Assert.True(payload.GetProperty("is_live").GetBoolean());
        Assert.False(payload.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public void AgentEventEnvelope_ReadsTheOrchestratorsJson()
    {
        const string wire = """
            {"type":"agent.turn.completed","tenant_id":"owner-1","conversation_key":"whatsapp:2348012345678:2349011112222","turn_id":"turn_1",
             "occurred_at":"2026-10-08T12:00:00+00:00","correlation_id":"abc","schema_version":1,
             "payload":{"status":"completed","decision":"PERMIT","confidence":0.9,"reply":"Hi","delivered":true,"channel":"whatsapp","customer_ref":"2348012345678","customer_name":null,"route":[],"escalation_id":null,"approval_id":null,"cost_usd":0.001,"latency_ms":100,"message_id":"m1","agent_id":"a1","intent":"greeting"}}
            """;

        AgentEventEnvelope envelope = JsonSerializer.Deserialize<AgentEventEnvelope>(wire, MessageJson.Options)!;

        Assert.Equal("agent.turn.completed", envelope.Type);
        Assert.Equal("owner-1", envelope.TenantId);
        Assert.Equal("turn_1", envelope.TurnId);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.True(AgentActivityMapper.TryMap(envelope, DateTime.UtcNow, out AgentActivityRecord? record, out _));
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), record!.OccurredAt);
        Assert.Null(record.CustomerName);
        Assert.Equal("a1", record.AgentId);
        Assert.Null(record.Contributors);
    }

    // A team reply names the hidden super agent; the agents it asked come as ids and status only. They are kept even
    // when a long reply makes the payload too big to store.
    [Fact]
    public void AgentEventEnvelope_ATeamReply_KeepsTheAgentsItAskedAndHowEachDid()
    {
        string reply = new('x', AgentActivityMapper.MaxPayloadBytes);
        string wire = $$$"""
            {"type":"agent.turn.completed","tenant_id":"owner-1","turn_id":"turn_1","occurred_at":"2026-10-08T12:00:00+00:00",
             "payload":{"status":"completed","agent_id":"agt_super","reply":"{{{reply}}}","contributors":[
               {"agent_id":"agt_tobi","status":"answered"},{"status":"answered"},{"agent_id":"agt_kora","status":"TIMED_OUT"},
               {"agent_id":"agt_nia"},{"agent_id":"agt_fourth","status":"answered"}]}}
            """;

        AgentEventEnvelope envelope = JsonSerializer.Deserialize<AgentEventEnvelope>(wire, MessageJson.Options)!;

        Assert.True(AgentActivityMapper.TryMap(envelope, DateTime.UtcNow, out AgentActivityRecord? record, out _));
        Assert.Equal("agt_super", record!.AgentId);
        Assert.Equal(
            [new TurnContributor("agt_tobi", "answered"), new TurnContributor("agt_kora", "timed_out"), new TurnContributor("agt_nia", "unknown")],
            record.Contributors);
        Assert.Equal("{}", record.Payload);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"agt_tobi\"")]
    [InlineData("[{\"status\":\"answered\"},7]")]
    public void AgentEventEnvelope_ContributorsItCannotRead_AreNone(string contributors)
    {
        string wire = $$$"""
            {"type":"agent.turn.completed","tenant_id":"owner-1","turn_id":"turn_1",
             "payload":{"status":"completed","agent_id":"agt_super","contributors":{{{contributors}}}}}
            """;

        AgentEventEnvelope envelope = JsonSerializer.Deserialize<AgentEventEnvelope>(wire, MessageJson.Options)!;

        Assert.True(AgentActivityMapper.TryMap(envelope, DateTime.UtcNow, out AgentActivityRecord? record, out _));
        Assert.Null(record!.Contributors);
    }

    [Fact]
    public void KafkaSettings_NameTheAgentEventsTopicAndGroup()
    {
        KafkaSettings settings = new();

        Assert.Equal("agent.events", settings.AgentEventsTopic);
        Assert.Equal("parrot-agent-events", settings.AgentEventsConsumerGroupId);
    }
}
