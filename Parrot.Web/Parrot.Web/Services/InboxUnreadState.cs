using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Parrot.Application.Messaging;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services.Realtime;

namespace Parrot.Web.Services;

/// <summary>
/// How many customer messages are waiting, across every conversation and channel: the
/// number on the rail's Inbox item. It lives apart from the inbox page so it is right on
/// every dashboard page, not only while the inbox is open.
///
/// The server is the record (per user, saved in MongoDB). This keeps a copy and moves it
/// on each live push, then replaces it with the server's number: on start, after a
/// reconnect, on a timer, and whenever a thread is marked read.
/// </summary>
public sealed class InboxUnreadState(ConversationClient client, RealtimeClient realtime, AuthState auth,
    ILogger<InboxUnreadState> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectedPoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisconnectedPoll = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private bool _started;
    private int _total;

    public int Total => Volatile.Read(ref _total);

    /// <summary>The total changed. May be raised from any thread.</summary>
    public event Action? Changed;

    /// <summary>Safe to call again, for instance once the user signs in.</summary>
    public async Task StartAsync()
    {
        if (!auth.IsAuthenticated) return;
        bool first;
        lock (_gate) { first = !_started; _started = true; }
        if (first)
        {
            realtime.MessageReceived += OnMessageReceived;
            realtime.Reconnected += RefreshAsync;
            _ = Task.Run(() => PollAsync(_lifetime.Token));
        }
        await realtime.StartAsync();
        await RefreshAsync();
    }

    /// <summary>Replaces the total with the server's.</summary>
    public async Task RefreshAsync()
    {
        if (!auth.IsAuthenticated)
        {
            Set(0);
            return;
        }
        try { Set((await client.UnreadAsync(_lifetime.Token)).Total); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            // Keeps the last number; the next poll tries again.
            logger.LogDebug(ex, "inbox.unread_refresh_failed");
        }
    }

    /// <summary>The server's total, as returned when a thread is marked read.</summary>
    public void Set(int total)
    {
        total = Math.Max(0, total);
        if (Interlocked.Exchange(ref _total, total) != total) Changed?.Invoke();
    }

    /// <summary>Moves the total by a known amount, ahead of the server's answer.</summary>
    public void Adjust(int delta)
    {
        if (delta == 0) return;
        int current, next;
        do
        {
            current = Volatile.Read(ref _total);
            next = Math.Max(0, current + delta);
        }
        while (Interlocked.CompareExchange(ref _total, next, current) != current);
        if (next != current) Changed?.Invoke();
    }

    // A customer's message adds one. The business's own replies never do. A message into the
    // thread the agent is looking at is marked read by the inbox page, whose answer replaces this.
    public Task OnMessageReceived(StoredMessage message)
    {
        if (!message.Outgoing) Adjust(1);
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool connected = realtime.State == HubConnectionState.Connected;
                await Task.Delay(connected ? ConnectedPoll : DisconnectedPoll, ct);
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        realtime.MessageReceived -= OnMessageReceived;
        realtime.Reconnected -= RefreshAsync;
        _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}
