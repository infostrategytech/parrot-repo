using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

public class OmniAgentStoreTests
{
    private static readonly Guid WhatsApp = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Instagram = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task CreateAssignsEveryConnectionOfTheKindsSwitchedOn()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/channels", Page());
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"},{"id":"{{Instagram}}","channel":"instagram"}]""");
        f.Omni.On("POST", "api/v1/agents", Agent("agt_1", version: 1), HttpStatusCode.Created);

        AiAgent created = await f.Store.CreateAsync(NewAgent("whatsapp"));

        Recorded post = f.Omni.Single("POST", "api/v1/agents");
        using JsonDocument body = JsonDocument.Parse(post.Body!);
        Assert.Equal("support", body.RootElement.GetProperty("use_case").GetString());
        Assert.Equal([WhatsApp.ToString()], body.RootElement.GetProperty("connection_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("warm", body.RootElement.GetProperty("settings").GetProperty("tone").GetString());
        Assert.False(string.IsNullOrEmpty(post.Header("Idempotency-Key")));
        Assert.Equal("Bearer session-token", post.Header("Authorization"));
        Assert.Equal("agt_1", created.Id);
        Assert.Equal(["whatsapp"], created.Channels.Where(c => c.IsEnabled).Select(c => c.Key));
    }

    [Fact]
    public async Task CreateAssignsANumberAnotherAgentAlreadyAnswersOn()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/channels", Page(Channel(WhatsApp, "agt_2", "whatsapp", version: 1)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        f.Omni.On("POST", "api/v1/agents", Agent("agt_1", version: 1), HttpStatusCode.Created);

        AiAgent created = await f.Store.CreateAsync(NewAgent("whatsapp"));

        using JsonDocument body = JsonDocument.Parse(f.Omni.Single("POST", "api/v1/agents").Body!);
        Assert.Equal([WhatsApp.ToString()], body.RootElement.GetProperty("connection_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["whatsapp"], created.Channels.Where(c => c.IsEnabled).Select(c => c.Key));
    }

    [Fact]
    public async Task SaveAddsTheAgentToANumberAnotherAgentAlreadyAnswersOn()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3), Agent("agt_2", name: "Bola", version: 1)));
        f.Omni.On("GET", "api/v1/channels", Page(Channel(WhatsApp, "agt_2", "whatsapp", version: 1)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("POST", "api/v1/channels", Channel(WhatsApp, "agt_1", "whatsapp", version: 1), HttpStatusCode.Created);
        AiAgent agent = (await f.Store.ListAsync()).Single(a => a.Id == "agt_1");
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        await f.Store.SaveAsync(agent);

        using JsonDocument body = JsonDocument.Parse(f.Omni.Single("POST", "api/v1/channels").Body!);
        Assert.Equal("agt_1", body.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal(WhatsApp.ToString(), body.RootElement.GetProperty("connection_id").GetString());
        Assert.DoesNotContain(f.Omni.Requests, r => r.Path.Contains("agt_2"));
    }

    [Fact]
    public async Task SaveSendsTheListedVersionAndMovesItsChannelsToTheKindsSwitchedOn()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page(Channel(WhatsApp, "agt_1", "whatsapp", version: 2)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"},{"id":"{{Instagram}}","channel":"instagram"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("POST", "api/v1/channels", Channel(Instagram, "agt_1", "instagram", version: 1), HttpStatusCode.Created);
        f.Omni.On("DELETE", $"api/v1/channels/{WhatsApp}/agents/agt_1", $$"""{"connection_id":"{{WhatsApp}}","agent_id":"agt_1","deleted":true,"version":3}""");
        AiAgent agent = (await f.Store.ListAsync()).Single();
        agent.Channels = AgentBuilderCopy.DefaultChannels(["instagram"]);

        await f.Store.SaveAsync(agent);

        Assert.Equal("3", f.Omni.Single("PATCH", "api/v1/agents/agt_1").Header("If-Match"));
        Assert.Contains(Instagram.ToString(), f.Omni.Single("POST", "api/v1/channels").Body);
        Assert.Equal("2", f.Omni.Single("DELETE", $"api/v1/channels/{WhatsApp}/agents/agt_1").Header("If-Match"));
    }

    [Fact]
    public async Task SaveOnSharedNumbersChangesOnlyTheAgentsOwnLinks()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3), Agent("agt_2", name: "Bola", version: 1)));
        f.Omni.On("GET", "api/v1/channels", Page(
            Channel(WhatsApp, "agt_1", "whatsapp", version: 2, enabled: false),
            Channel(WhatsApp, "agt_2", "whatsapp", version: 5),
            Channel(Instagram, "agt_1", "instagram", version: 3),
            Channel(Instagram, "agt_2", "instagram", version: 6)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"},{"id":"{{Instagram}}","channel":"instagram"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("PATCH", $"api/v1/channels/{WhatsApp}/agents/agt_1", Channel(WhatsApp, "agt_1", "whatsapp", version: 3));
        f.Omni.On("DELETE", $"api/v1/channels/{Instagram}/agents/agt_1", $$"""{"connection_id":"{{Instagram}}","agent_id":"agt_1","deleted":true,"version":4}""");
        AiAgent agent = (await f.Store.ListAsync()).Single(a => a.Id == "agt_1");
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        await f.Store.SaveAsync(agent);

        Recorded resumed = f.Omni.Single("PATCH", $"api/v1/channels/{WhatsApp}/agents/agt_1");
        Assert.Equal("""{"enabled":true}""", resumed.Body);
        Assert.Equal("2", resumed.Header("If-Match"));
        Assert.Equal("3", f.Omni.Single("DELETE", $"api/v1/channels/{Instagram}/agents/agt_1").Header("If-Match"));
        Assert.DoesNotContain(f.Omni.Requests, r => r.Method == "POST");
        Assert.DoesNotContain(f.Omni.Requests, r => r.Path.Contains("agt_2"));
    }

    [Fact]
    public async Task ListShowsEachAgentOnlyTheNumbersItIsSwitchedOnFor()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1"), Agent("agt_2", name: "Bola")));
        f.Omni.On("GET", "api/v1/channels", Page(
            Channel(WhatsApp, "agt_1", "whatsapp", version: 1),
            Channel(WhatsApp, "agt_2", "whatsapp", version: 1, enabled: false),
            Channel(Instagram, "agt_2", "instagram", version: 1)));

        IReadOnlyList<AiAgent> agents = await f.Store.ListAsync();

        Assert.Equal(["whatsapp"], agents.Single(a => a.Id == "agt_1").Channels.Where(c => c.IsEnabled).Select(c => c.Key));
        Assert.Equal(["instagram"], agents.Single(a => a.Id == "agt_2").Channels.Where(c => c.IsEnabled).Select(c => c.Key));
    }

    [Fact]
    public async Task SaveNeverSaysAChannelAnswersElsewhereWhenTheAiServiceRefusesIt()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page());
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("POST", "api/v1/channels", Refused("CHANNEL_TAKEN", "This channel answers in another workspace."), HttpStatusCode.Conflict);
        AiAgent agent = (await f.Store.ListAsync()).Single();
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => f.Store.SaveAsync(agent));

        Assert.Equal(OmniAgentStore.ChannelUnavailable, error.UserMessage);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.DoesNotContain("workspace", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateNeverSaysAChannelAnswersElsewhereWhenTheAiServiceRefusesIt()
    {
        Fixture f = new();
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        f.Omni.On("POST", "api/v1/agents", Refused("CHANNEL_TAKEN", "This channel answers in another workspace."), HttpStatusCode.Conflict);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => f.Store.CreateAsync(NewAgent("whatsapp")));

        Assert.Equal(OmniAgentStore.ChannelUnavailable, error.UserMessage);
    }

    [Fact]
    public async Task WhileAgentsCantShareChannelsSaveRefusesAKindAnotherAgentAnswersOnBeforeWritingAnything()
    {
        Fixture f = new(sharesChannels: false);
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3), Agent("agt_2", name: "Bola", version: 1)));
        f.Omni.On("GET", "api/v1/channels", Page(Channel(WhatsApp, "agt_2", "whatsapp", version: 1)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        AiAgent agent = (await f.Store.ListAsync()).Single(a => a.Id == "agt_1");
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => f.Store.SaveAsync(agent));

        Assert.Equal("CHANNEL_TAKEN", error.ErrorCode);
        Assert.Contains("Bola", error.Message);
        Assert.DoesNotContain(f.Omni.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task WhileAgentsCantShareChannelsSaveChangesLinksByConnectionAlone()
    {
        Fixture f = new(sharesChannels: false);
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page(
            Channel(WhatsApp, "agt_1", "whatsapp", version: 2, enabled: false), Channel(Instagram, "agt_1", "instagram", version: 3)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"},{"id":"{{Instagram}}","channel":"instagram"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("PATCH", $"api/v1/channels/{WhatsApp}", Channel(WhatsApp, "agt_1", "whatsapp", version: 3));
        f.Omni.On("DELETE", $"api/v1/channels/{Instagram}", $$"""{"connection_id":"{{Instagram}}","agent_id":"agt_1","deleted":true,"version":4}""");
        AiAgent agent = (await f.Store.ListAsync()).Single();
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        await f.Store.SaveAsync(agent);

        Assert.Equal("2", f.Omni.Single("PATCH", $"api/v1/channels/{WhatsApp}").Header("If-Match"));
        Assert.Equal("3", f.Omni.Single("DELETE", $"api/v1/channels/{Instagram}").Header("If-Match"));
    }

    // The switch is read on every save, so turning it off on the Overview applies to the next save without a reload.
    [Fact]
    public async Task SaveAfterTheSwitchIsTurnedOffChangesLinksByConnectionAlone()
    {
        Fixture f = new(sharesChannels: true);
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page(
            Channel(WhatsApp, "agt_1", "whatsapp", version: 2, enabled: false), Channel(Instagram, "agt_1", "instagram", version: 3)));
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"},{"id":"{{Instagram}}","channel":"instagram"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("PATCH", $"api/v1/channels/{WhatsApp}", Channel(WhatsApp, "agt_1", "whatsapp", version: 3));
        f.Omni.On("DELETE", $"api/v1/channels/{Instagram}", $$"""{"connection_id":"{{Instagram}}","agent_id":"agt_1","deleted":true,"version":4}""");
        AiAgent agent = (await f.Store.ListAsync()).Single();
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        f.Parrot.On("GET", "api/multi-agent-switch", """{"isOffered":true,"isOn":false}""");
        await f.Store.SaveAsync(agent);

        Assert.Equal("2", f.Omni.Single("PATCH", $"api/v1/channels/{WhatsApp}").Header("If-Match"));
        Assert.Equal("3", f.Omni.Single("DELETE", $"api/v1/channels/{Instagram}").Header("If-Match"));
        Assert.DoesNotContain(f.Omni.Requests, r => r.Path.Contains("/agents/agt_1") && r.Path.StartsWith("api/v1/channels"));
    }

    [Theory]
    [InlineData("CHANNEL_ALREADY_ASSIGNED", "This agent is already on this channel.")]
    [InlineData("AGENT_REQUIRED", "Several agents are on this channel. Say which one.")]
    public async Task SaveShowsTheAiServicesOwnWordsWhenItRefusesAChannel(string errorCode, string message)
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page());
        f.Parrot.On("GET", "api/connections", $$"""[{"id":"{{WhatsApp}}","channel":"whatsapp"}]""");
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("POST", "api/v1/channels", Refused(errorCode, message), HttpStatusCode.Conflict);
        AiAgent agent = (await f.Store.ListAsync()).Single();
        agent.Channels = AgentBuilderCopy.DefaultChannels(["whatsapp"]);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => f.Store.SaveAsync(agent));

        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal(message, error.UserMessage);
    }

    [Fact]
    public async Task SetLiveSendsTheVersionTheLastSaveReturned()
    {
        Fixture f = new();
        f.Omni.On("GET", "api/v1/agents", Page(Agent("agt_1", version: 3)));
        f.Omni.On("GET", "api/v1/channels", Page());
        f.Omni.On("PATCH", "api/v1/agents/agt_1", Agent("agt_1", version: 4));
        f.Omni.On("PATCH", "api/v1/agents/agt_1/live", Agent("agt_1", version: 5, live: true));
        AiAgent agent = (await f.Store.ListAsync()).Single();

        await f.Store.SaveAsync(agent);
        await f.Store.SetLiveAsync("agt_1", true);

        Recorded live = f.Omni.Single("PATCH", "api/v1/agents/agt_1/live");
        Assert.Equal("4", live.Header("If-Match"));
        Assert.Equal("""{"is_live":true}""", live.Body);
    }

    [Theory]
    [InlineData("support", AgentRoleTemplate.Support)]
    [InlineData("followups", AgentRoleTemplate.Sales)]
    [InlineData("tracking", AgentRoleTemplate.Orders)]
    [InlineData("bookings", AgentRoleTemplate.Custom)]
    public void TheJobRoleKeepsItsIdAndGivesTheScreensTheirRole(string useCase, AgentRoleTemplate role)
    {
        OmniAgent agent = JsonSerializer.Deserialize<OmniAgent>(Agent("agt_1", useCase: useCase), OmniAiClient.Json)!;

        AiAgent mapped = OmniAgentMapping.ToAgent(agent, []);

        Assert.Equal(role, mapped.Settings.Role);
        Assert.Equal(useCase, OmniAgentMapping.ToUpdate(mapped).UseCase);
        Assert.Equal(AgentVoicePreset.Professional, mapped.Settings.Tone);
        Assert.True(mapped.IsDraft);
    }

    [Fact]
    public void AnAgentFromBeforeUseCasesIsSentItsRolesJob()
    {
        AiAgent agent = NewAgent();
        agent.Settings.UseCase = null;
        agent.Settings.Role = AgentRoleTemplate.Orders;

        Assert.Equal("tracking", OmniAgentMapping.ToUpdate(agent).UseCase);
    }

    private static AiAgent NewAgent(params string[] channels) => new()
    {
        Name = "Ada",
        Mascot = "Nova",
        Channels = AgentBuilderCopy.DefaultChannels(channels),
        Settings = new AgentSettings { UseCase = "support" },
    };

    private static string Page(params string[] items) => $$"""{"items":[{{string.Join(",", items)}}],"next_cursor":null}""";

    private static string Refused(string errorCode, string message) =>
        $$"""{"success":false,"errorCode":"{{errorCode}}","message":"{{message}}","traceId":"t-1"}""";

    private static string Channel(Guid id, string agentId, string platform, int version, bool enabled = true) =>
        $$"""{"connection_id":"{{id}}","agent_id":"{{agentId}}","platform":"{{platform}}","enabled":{{(enabled ? "true" : "false")}},"version":{{version}}}""";

    private static string Agent(string id, string name = "Ada", int version = 1, bool live = false, string useCase = "support") => $$"""
        {"agent_id":"{{id}}","name":"{{name}}","use_case":"{{useCase}}","role":null,"description":"","avatar":"Nova",
         "human_takeover":false,"brief":"","industry":"","website":"","status":"{{(live ? "published" : "draft")}}","is_live":{{(live ? "true" : "false")}},
         "settings":{"tone":"professional","length":"balanced","emoji":"light","greeting":"","avoid_phrases":[],"handover_to":"",
           "can_offer_discounts":false,"can_approve_refunds":false,"can_recommend":true,"can_check_orders":false,"can_take_details":true,
           "hand_over_when_upset":true,"hand_over_after_misses":true,"hand_over_big_orders":false,"big_order_naira":50000,"notes":"","left_out":[]},
         "connection_ids":[],"channels":[],"version":{{version}},"created_at":"2026-10-29T10:00:00+00:00","updated_at":"2026-10-29T10:00:00+00:00"}
        """;

    private sealed class Fixture
    {
        public Routes Omni { get; } = new();
        public Routes Parrot { get; } = new();
        public OmniAgentStore Store { get; }

        public Fixture(bool sharesChannels = true)
        {
            AuthState auth = new();
            auth.SetUser(new AuthResponse { Token = "session-token", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            // The workspace's switch for agents sharing a number, as the API answers it.
            Parrot.On("GET", "api/multi-agent-switch", $$"""{"isOffered":true,"isOn":{{(sharesChannels ? "true" : "false")}}}""");
            HttpClient parrot = new(Parrot) { BaseAddress = new Uri("http://parrot.test/") };
            Store = new OmniAgentStore(
                new OmniAiClient(new HttpClient(Omni) { BaseAddress = new Uri("http://omni.test/") }, auth),
                parrot,
                auth,
                new MultiAgentSwitchClient(parrot, auth));
        }
    }
}

public sealed record Recorded(string Method, string Path, string? Body, Dictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Answers by method and path (query ignored), and keeps every request it was sent.</summary>
public sealed class Routes : HttpMessageHandler
{
    private readonly Dictionary<(string, string), (string Body, HttpStatusCode Status)> _routes = [];

    public List<Recorded> Requests { get; } = [];

    public void On(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[(method, path)] = (body, status);

    public Recorded Single(string method, string path) => Assert.Single(Requests, r => r.Method == method && r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        Dictionary<string, string> headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Recorded(request.Method.Method, path, body, headers));
        if (!_routes.TryGetValue((request.Method.Method, path), out var route))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No route for {request.Method} {path}") };
        return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
    }
}
