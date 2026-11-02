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
