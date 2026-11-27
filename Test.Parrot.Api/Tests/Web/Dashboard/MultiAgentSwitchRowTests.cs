using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.Application.Contracts.Agents;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Dashboard;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents;

namespace Test.Parrot.Api.Tests.Web.Dashboard;

// The live test's switch for agents sharing a number on the Overview: for the test workspaces, for people who may edit agents.
public sealed class MultiAgentSwitchRowTests
{
    private sealed class Api(string body) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body, string? Token)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }

    private static AuthState SignedIn(string? role = null, params string[] permissions)
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "session-token", Role = role ?? "", Permissions = [.. permissions], ExpiresAt = DateTime.UtcNow.AddHours(1) });
        return auth;
    }

    private static MultiAgentSwitchClient Client(Api api, AuthState auth) =>
        new(new HttpClient(api) { BaseAddress = new Uri("http://parrot.test/") }, auth);

    private static async Task<string> Render(Api api, AuthState auth)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton<AuroraInterop>().AddSingleton<ToastService>().AddSingleton(auth).AddSingleton(Client(api, auth))
            .BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<MultiAgentSwitchRow>(ParameterView.Empty);
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    [Theory]
    [InlineData(true, "On: several agents can answer on one number.")]
    [InlineData(false, "Off: one agent answers on each number.")]
    public async Task Render_ForATestWorkspaceAndTheOwner_ShowsTheSwitchAndWhetherItIsOn(bool isOn, string line)
    {
        Api api = new($$"""{"isOffered":true,"isOn":{{(isOn ? "true" : "false")}}}""");

        string html = await Render(api, SignedIn());

        Assert.Contains("Agents Sharing a Number", html);
        Assert.Contains(line, html);
        Assert.Contains("aria-label=\"Agents sharing a number\"", html);
        Assert.Equal(("GET", "/api/multi-agent-switch", (string?)null, (string?)"session-token"), Assert.Single(api.Requests));
    }

    [Fact]
    public async Task Render_ForAWorkspaceNotInTheTest_ShowsNothing()
    {
        string html = await Render(new Api("""{"isOffered":false,"isOn":false}"""), SignedIn());

        Assert.DoesNotContain("multi-agent-switch", html);
    }

    [Fact]
    public async Task Render_ForSomeoneWhoMayNotEditAgents_ShowsNothingAndNeverAsks()
    {
        Api api = new("""{"isOffered":true,"isOn":true}""");

        string html = await Render(api, SignedIn("Member", "conversations.view"));

        Assert.DoesNotContain("multi-agent-switch", html);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task SetAsync_SendsTheChoiceForTheSignedInWorkspace()
    {
        Api api = new("""{"isOffered":true,"isOn":false}""");

        MultiAgentSwitchResponse saved = await Client(api, SignedIn()).SetAsync(false);

        Assert.False(saved.IsOn);
        (string method, string path, string? body, string? token) = Assert.Single(api.Requests);
        Assert.Equal(("PUT", "/api/multi-agent-switch", "session-token"), (method, path, token));
        Assert.Equal("""{"isOn":false}""", body);
    }
}
