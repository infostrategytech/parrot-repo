using System.Text.Json;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

public class OrientationApiTests
{
    [Fact]
    public async Task FormsAreReadFromTheAgentsEventsOnly()
    {
        const string stream = """
            : connected

            id: 1-0
            event: orientation.form.agt_other
            data: {"review_id":"r-0","form":"{\"id\":\"x\"}"}

            id: 1793200000000-0
            event: orientation.form.agt_1
            data: {"review_id":"review-123","form":"{\"id\":\"agent-behaviour\",\"questions\":[]}"}

            : keep-alive

            id: 2-0
            event: orientation.form.agt_1
            data: not json

            """;
        (OrientationApi api, Routes routes) = Build();
        routes.On("GET", "api/v1/orientation/forms/stream/agt_1", stream.Replace("\r\n", "\n"));

        List<OrientationForm> forms = [];
        await foreach (OrientationForm form in api.ReadFormsAsync("agt_1")) forms.Add(form);

        OrientationForm only = Assert.Single(forms);
        Assert.Equal("1793200000000-0", only.EventId);
        Assert.Equal("review-123", only.ReviewId);
        Assert.Equal("agent-behaviour", only.Form.GetProperty("id").GetString());
        Assert.Equal("text/event-stream", routes.Single("GET", "api/v1/orientation/forms/stream/agt_1").Header("Accept"));
    }

    [Fact]
    public async Task ReadProgressAsync_WhenProgressChanges_GivesItNowThenEachChangeForThisAgentOnly()
    {
        // The middle event is the AI team's sample (Orientation Progress Stream API, 23 November 2026).
        const string stream = """
            : connected

            id: 1-0
            event: orientation.progress.agt_other
            data: {"agent_id":"agt_other","percent":5,"ready":false,"required_count":7,"reviewed_count":0}

            id: 1732361200000-0
            event: orientation.progress.agt_1
            data: {"agent_id":"agt_1","percent":42,"ready":false,"complete":false,"can_activate":false,"stage":"discovering","required":["products_services","prices","payment_methods","delivery","opening_hours","returns_refunds","handover_contact"],"reviewed":["products_services","prices"],"remaining_required":["payment_methods","delivery","opening_hours","returns_refunds","handover_contact"],"pending_review":[],"generating_review":[],"required_count":7,"reviewed_count":2,"total_topics":8,"active_topic":{"key":"payment_methods","label":"Payment methods","required":true,"stage":"in_progress","questions_asked":1,"details_covered":1,"details_total":2},"topics":[{"key":"products_services","label":"Products and services","required":true,"status":"reviewed","questions_asked":2,"details_covered":2,"details_total":2}],"updated_at":"2026-11-23T12:00:00+00:00"}

            : keep-alive

            id: 2-0
            event: orientation.progress.agt_1
            data: not json

            id: snapshot
            event: orientation.progress.agt_1
            data: {"agent_id":"agt_1","percent":100,"ready":true,"complete":true,"can_activate":true,"stage":"complete","required_count":7,"reviewed_count":7}


            """;
        (OrientationApi api, Routes routes) = Build();
        routes.On("GET", "api/v1/orientation/progress/stream/agt_1", stream.Replace("\r\n", "\n"));

        List<OrientationProgress> snapshots = [];
        await foreach (OrientationProgress progress in api.ReadProgressAsync("agt_1")) snapshots.Add(progress);

        Assert.Equal(2, snapshots.Count);
        OrientationProgress sample = snapshots[0];
        Assert.Equal((42, false, 7, 2, false), (sample.Percent, sample.Ready, sample.RequiredCount, sample.ReviewedCount, sample.CanActivate));
        Assert.Empty(sample.PendingReview!);
        Assert.Empty(sample.GeneratingReview!);
        Assert.Equal(new OrientationTopic("payment_methods", "Payment methods", true, null, 1, 2), sample.ActiveTopic);
        Assert.Equal(new OrientationTopic("products_services", "Products and services", true, "reviewed", 2, 2), Assert.Single(sample.Topics!));
        // A snapshot without the topic lists still reads.
        Assert.Equal(new OrientationProgress(100, true, 7, 7, CanActivate: true), snapshots[1]);
        Assert.Equal("text/event-stream", routes.Single("GET", "api/v1/orientation/progress/stream/agt_1").Header("Accept"));
        Assert.Equal("Bearer session-token", routes.Single("GET", "api/v1/orientation/progress/stream/agt_1").Header("Authorization"));
    }

    [Fact]
    public async Task GetProgressAsync_WhenAsked_ReadsTheProgressNowAsTheSignedInOwner()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("GET", "api/v1/orientation/progress/agt_1/detail",
            """{"agent_id":"agt_1","percent":30,"ready":false,"stage":"discovering","required_count":7,"reviewed_count":1}""");

        OrientationProgress progress = await api.GetProgressAsync("agt_1");

        Assert.Equal(new OrientationProgress(30, false, 7, 1), progress);
        Assert.Equal("Bearer session-token", routes.Single("GET", "api/v1/orientation/progress/agt_1/detail").Header("Authorization"));
    }

    [Fact]
    public async Task ReadProgressAsync_WhenTheAgentIsNotFound_ThrowsNotFoundSoTheScreenStopsAsking()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("GET", "api/v1/orientation/progress/stream/agt_1",
            """{"errorCode":"NOT_FOUND","message":"Agent not found in the requested scope"}""", System.Net.HttpStatusCode.NotFound);

        ApiException error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (OrientationProgress _ in api.ReadProgressAsync("agt_1")) { }
        });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task AnAnswerGoesAsAJsonString()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("POST", "api/v1/orientation/forms/answer/agt_1", "true");
        OrientationForm form = new("1-0", "review-123", JsonDocument.Parse("{}").RootElement);

        await api.AnswerFormAsync("agt_1", form, new Dictionary<string, object?> { ["tone"] = "Warm and friendly", ["approval"] = true });

        using JsonDocument body = JsonDocument.Parse(routes.Single("POST", "api/v1/orientation/forms/answer/agt_1").Body!);
        Assert.Equal("1-0", body.RootElement.GetProperty("event_id").GetString());
        Assert.Equal("review-123", body.RootElement.GetProperty("review_id").GetString());
        Assert.Equal("""{"tone":"Warm and friendly","approval":true}""", body.RootElement.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task TheAnswerToATurnArrivesAsItsPieces()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("POST", "api/v1/orientation/text/agt_1", "Got it, you open at nine.");

        string answer = "";
        await foreach (string piece in api.SendTextAsync("agt_1", "Our shop opens at nine.")) answer += piece;

        Assert.Equal("Got it, you open at nine.", answer);
        Assert.Contains("Our shop opens at nine.", routes.Single("POST", "api/v1/orientation/text/agt_1").Body);
    }

    [Fact]
    public async Task TheAiCanSpeakFirstWithNothingSent()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("POST", "api/v1/orientation/text/agt_1", "Hi, I'm Tolu. Tell me about my job.");

        string opening = "";
        await foreach (string piece in api.StartTextAsync("agt_1")) opening += piece;

        Assert.Equal("Hi, I'm Tolu. Tell me about my job.", opening);
        string body = routes.Single("POST", "api/v1/orientation/text/agt_1").Body!;
        Assert.Contains("name=trigger_user", body);
        Assert.Contains("true", body);
        Assert.DoesNotContain("name=text", body);
        Assert.DoesNotContain("name=file", body);
    }

    [Fact]
    public async Task AnOrientationErrorShowsItsMessage()
    {
        (OrientationApi api, Routes routes) = Build();
        routes.On("POST", "api/v1/orientation/voice/session/agt_1",
            """{"detail":{"errorCode":"ONBOARDING_DISABLED","message":"Orientation is switched off."}}""", System.Net.HttpStatusCode.ServiceUnavailable);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => api.StartVoiceAsync("agt_1"));

        Assert.Equal("ONBOARDING_DISABLED", error.ErrorCode);
        Assert.Equal("Orientation is switched off.", error.Message);
    }

    private static (OrientationApi, Routes) Build()
    {
        Routes routes = new();
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "session-token", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        Uri baseUrl = new("http://omni.test/");
        return (new OrientationApi(
            new OmniAiClient(new HttpClient(routes) { BaseAddress = baseUrl }, auth),
            new OmniAiStreams(new HttpClient(routes) { BaseAddress = baseUrl })), routes);
    }
}
