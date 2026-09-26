namespace Parrot.Web.Services;

// Counts a request for as long as it is outstanding. HttpClient's default
// completion option reads the body before SendAsync returns, so for ordinary
// calls this spans the whole wait, not just the headers.
public sealed class ActivityHandler(ActivityState activity) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        activity.Begin();
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            activity.End();
        }
    }
}
