using System.Net;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

// Created in the Blazor circuit scope, not HttpClientFactory's separate handler scope.
public sealed class SessionExpiryHandler(AuthState authState) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            authState.ReportUnauthorized(request.Headers.Authorization?.Parameter);
        }
        return response;
    }
}
