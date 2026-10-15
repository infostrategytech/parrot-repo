using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Parrot.Application.Agents;
using Parrot.Application.Messaging;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services.Realtime;

/// <summary>
/// This circuit's connection to the API's messaging hub. Connecting registers
/// the signed-in user online; a heartbeat keeps them online; SignalR's
/// automatic reconnect gets a new connection, which the hub registers again.
///
/// Live pushes are a shortcut, not the record: whenever the connection comes
/// back after a gap, <see cref="Reconnected"/> fires so the page can fetch
/// what it missed from history.
/// </summary>
public sealed class RealtimeClient(AuthState auth, IConfiguration configuration, ILogger<RealtimeClient> logger) : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly SeenMessages _seen = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private HubConnection? _connection;
    // Set while the browser is gone but the circuit is kept for a possible return.
    private volatile bool _paused;

    /// <summary>A newly persisted message for this user, once per message id.</summary>
    public event Func<StoredMessage, Task>? MessageReceived;

    /// <summary>A channel reported receipts (delivered, read) for replies in a conversation.</summary>
    public event Func<ReceiptsChanged, Task>? ReceiptsChanged;

    /// <summary>One of a customer's files finished loading (ready, failed or too large).</summary>
    public event Func<MediaChanged, Task>? MediaChanged;

    /// <summary>The connection is back after a drop; anything sent meanwhile was missed live.</summary>
    public event Func<Task>? Reconnected;

    /// <summary>An agent of this user's reported something: a turn, an approval it needs, an escalation.</summary>
    public event Func<AgentActivityEvent, Task>? AgentActivityReceived;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>Connects once per circuit. Safe to call from every page that wants pushes.</summary>
    public async Task StartAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (_connection is not null || !auth.IsAuthenticated) return;
            _connection = Build();
            _ = Task.Run(() => KeepConnectedAsync(_lifetime.Token));
            _ = Task.Run(() => HeartbeatAsync(_lifetime.Token));
        }
        finally
        {
            _startGate.Release();
        }
    }

    private HubConnection Build()
    {
        string hubUrl = new Uri(new Uri(configuration["ApiSettings:BaseUrl"]!), "hubs/messaging").ToString();
        HubConnection connection = new HubConnectionBuilder()
            // Read on every (re)connect, so a refreshed token is used.
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult(auth.CurrentUser?.Token))
            .WithAutomaticReconnect(new RetryForever())
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        connection.On<StoredMessage>("messagePersisted", async message =>
        {
            if (!_seen.TryAdd(message.MessageId)) return;
            if (MessageReceived is { } handler) await handler(message);
        });

        connection.On<ReceiptsChanged>("receiptsChanged", async change =>
        {
            if (ReceiptsChanged is { } handler) await handler(change);
        });

        connection.On<MediaChanged>("mediaChanged", async change =>
        {
            if (MediaChanged is { } handler) await handler(change);
        });

        // Not deduplicated here: the screens re-read their figures on it rather than apply it.
        connection.On<AgentActivityEvent>("agentActivity", async activity =>
        {
            if (AgentActivityReceived is { } handler) await handler(activity);
        });

        connection.Reconnected += async _ =>
        {
            logger.LogInformation("realtime.reconnected");
            if (Reconnected is { } handler) await handler();
        };

        return connection;
    }

    // Covers what automatic reconnect does not: the first connect failing
    // (API not up yet) and a connection that closed for good.
    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        int attempt = 0;
        bool wasConnected = false;
        while (!ct.IsCancellationRequested)
        {
            if (_connection!.State == HubConnectionState.Disconnected && auth.IsAuthenticated && !_paused)
            {
                try
                {
                    await _connection.StartAsync(ct);
                    logger.LogInformation("realtime.connected");
                    attempt = 0;
                    if (wasConnected && Reconnected is { } handler) await handler();
                    wasConnected = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "realtime.connect_failed Attempt={Attempt}", attempt + 1);
                    await DelayAsync(RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)], ct);
                    continue;
                }
            }

            await DelayAsync(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>
    /// The browser has gone; Blazor keeps the circuit a few minutes in case it
    /// returns. Disconnecting now takes the user offline at once, instead of
    /// heartbeats keeping a closed tab online until the circuit is dropped.
    /// </summary>
    public async Task PauseAsync()
    {
        _paused = true;
        if (_connection is { State: not HubConnectionState.Disconnected } connection)
        {
            try { await connection.StopAsync(); }
            catch (Exception ex) { logger.LogDebug(ex, "realtime.pause_failed"); }
        }
    }

    /// <summary>The browser is back: reconnect, and <see cref="Reconnected"/> lets the page fetch the gap.</summary>
    public void Resume() => _paused = false;

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromSeconds(configuration.GetValue("Realtime:HeartbeatIntervalSeconds", 20));
        using PeriodicTimer timer = new(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_connection?.State != HubConnectionState.Connected) continue;
                try { await _connection.InvokeAsync("Heartbeat", ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "realtime.heartbeat_failed; the session TTL covers the gap");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _lifetime.Dispose();
        _startGate.Dispose();
    }

    /// <summary>Keeps retrying while the circuit lives: 0, 2, 5, 10, then every 30 seconds.</summary>
    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            RetryDelays[Math.Min((int)context.PreviousRetryCount, RetryDelays.Length - 1)];
    }
}
