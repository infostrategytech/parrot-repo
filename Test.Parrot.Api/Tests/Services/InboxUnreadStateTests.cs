using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;
using Parrot.Web.Services.Realtime;

namespace Test.Parrot.Api.Tests.Services;

public class InboxUnreadStateTests
{
    private sealed class Server(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastPath = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static (InboxUnreadState State, Server Server, AuthState Auth) Create(string json = "{\"total\":7}", bool signedIn = true)
    {
        AuthState auth = new();
        if (signedIn)
            auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        Server server = new(json);
        ConversationClient client = new(new HttpClient(server) { BaseAddress = new Uri("http://api.test/") }, auth);
        RealtimeClient realtime = new(auth, new ConfigurationBuilder().Build(), NullLogger<RealtimeClient>.Instance);
        return (new InboxUnreadState(client, realtime, auth, NullLogger<InboxUnreadState>.Instance), server, auth);
    }

    private static StoredMessage Pushed(bool outgoing, MessagingPlatform platform = MessagingPlatform.WhatsApp) => new()
    {
        MessageId = Guid.NewGuid().ToString(), ConversationKey = "k", Sequence = 1, Platform = platform,
        ConnectionId = Guid.NewGuid(), Outgoing = outgoing, Sender = "customer", Receiver = "business",
        Type = "text", Text = "Hi", PersistedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task TheTotalIsTheServersNumber()
    {
        (InboxUnreadState state, Server server, _) = Create("{\"total\":7}");

        await state.RefreshAsync();

        Assert.Equal(7, state.Total);
        Assert.Equal("/api/conversations/unread", server.LastPath);
    }

    [Fact]
    public async Task SignedOutThereIsNothingToCountAndNoRequest()
    {
        (InboxUnreadState state, Server server, _) = Create(signedIn: false);
        state.Set(3);

        await state.RefreshAsync();

        Assert.Equal(0, state.Total);
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData(MessagingPlatform.WhatsApp)]
    [InlineData(MessagingPlatform.Facebook)]
    [InlineData(MessagingPlatform.Instagram)]
    [InlineData(MessagingPlatform.TikTok)]
    public async Task ACustomersPushOnAnyChannelAddsOneAndAReplyAddsNothing(MessagingPlatform platform)
    {
        (InboxUnreadState state, _, _) = Create();

        await state.OnMessageReceived(Pushed(outgoing: false, platform));
        await state.OnMessageReceived(Pushed(outgoing: true, platform));

        Assert.Equal(1, state.Total);
    }

    [Fact]
    public void TheTotalNeverGoesBelowZeroAndTellsListenersOnlyWhenItChanges()
    {
        (InboxUnreadState state, _, _) = Create();
        int changes = 0;
        state.Changed += () => changes++;

        state.Adjust(-5);
        state.Set(0);
        Assert.Equal(0, changes);

        state.Adjust(2);
        state.Set(10);
        state.Adjust(-4);
        Assert.Equal(6, state.Total);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheLastNumber()
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        ConversationClient client = new(new HttpClient(new Server("{}", HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("http://api.test/") }, auth);
        InboxUnreadState state = new(client, new RealtimeClient(auth, new ConfigurationBuilder().Build(), NullLogger<RealtimeClient>.Instance), auth,
            NullLogger<InboxUnreadState>.Instance);
        state.Set(4);

        await state.RefreshAsync();

        Assert.Equal(4, state.Total);
    }
}
