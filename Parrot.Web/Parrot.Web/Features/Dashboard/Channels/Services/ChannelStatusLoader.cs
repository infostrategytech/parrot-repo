using Parrot.Web.Features.Dashboard.Channels.State;
namespace Parrot.Web.Features.Dashboard.Channels.Services;

/// <summary>
/// Reads which channels are connected into <see cref="ChannelsState"/>, the
/// same two calls the Channels page makes, for pages that only need to know
/// whether anything is connected (the dashboard picks its view from it).
///
/// Channels.razor still has its own copy of this with page-specific error
/// messages; it can move onto this loader when that page is next touched.
/// </summary>
public sealed class ChannelStatusLoader(IIntegrationsService integrations, ChannelsState channels)
{
    /// <summary>
    /// Refreshes WhatsApp and Facebook status. Returns false if either could
    /// not be read. The state then keeps what it had, so a page treats an
    /// unreachable API as "nothing connected
    /// yet" unless a channel was connected earlier in this session.
    /// </summary>
    public async Task<bool> RefreshAsync(string token, CancellationToken cancellationToken)
    {
        bool ok = true;

        try
        {
            var connections = await integrations.GetWhatsAppConnectionsAsync(token, cancellationToken);
            channels.SetWhatsApp(connections.FirstOrDefault(c => c.DisabledAt is null) ?? connections.FirstOrDefault());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { ok = false; }

        try
        {
            channels.SetFacebook(await integrations.GetFacebookSetupAsync(token, cancellationToken));
        }
        // Not configured on the API server yet: nothing to show, which is the
        // same as "not connected".
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) { }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { ok = false; }

        return ok;
    }
}
