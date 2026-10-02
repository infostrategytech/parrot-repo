using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Parrot.Web.Services.Realtime;

/// <summary>Follows the browser's link to this circuit: offline when it drops, back online when it returns.</summary>
public sealed class RealtimeCircuitHandler(RealtimeClient realtime) : CircuitHandler
{
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => realtime.PauseAsync();

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        realtime.Resume();
        return Task.CompletedTask;
    }
}
