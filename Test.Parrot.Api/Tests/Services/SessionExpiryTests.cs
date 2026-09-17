using System.Net;
using System.Net.Http.Headers;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Services;

public class SessionExpiryTests
{
    [Theory]
    [InlineData(-10, false)]
    [InlineData(60, true)]
    public void AuthenticationRequiresUnexpiredToken(int seconds, bool authenticated)
    {
        AuthState state = new();
        state.SetUser(new AuthResponse { Token = "current", ExpiresAt = DateTime.UtcNow.AddSeconds(seconds) });
        Assert.Equal(authenticated, state.IsAuthenticated);
        state.ClearUser();
        Assert.False(state.IsAuthenticated);
    }

    [Theory]
    [InlineData(401, "current", true)]
    [InlineData(401, "old-token", false)]
    [InlineData(401, null, false)]
    [InlineData(403, "current", false)]
    [InlineData(200, "current", false)]
    public async Task OnlyUnauthorizedResponseForCurrentBearerExpiresSession(int status, string? token, bool expected)
    {
        AuthState state = new();
        state.SetUser(new AuthResponse { Token = "current", ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
        bool expired = false;
        state.SessionExpired += () => expired = true;
        using HttpClient client = new(new SessionExpiryHandler(state)
        {
            InnerHandler = new StubHandler((HttpStatusCode)status),
        });
        using HttpRequestMessage request = new(HttpMethod.Get, "https://example.test/resource");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(expected, expired);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
