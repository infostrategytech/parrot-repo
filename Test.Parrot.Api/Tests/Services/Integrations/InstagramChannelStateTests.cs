using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Dashboard.Channels.State;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The Channels card for Instagram follows what the API's setup says, as Facebook's does.
public class InstagramChannelStateTests
{
    private static InstagramWebhookSetup Setup(bool verified = true, bool token = true, string? account = "17841400000000000") =>
        new(Guid.NewGuid(), "https://api.example.com/api/webhooks/instagram?ref=abc", "verify", verified, account,
            account is null ? null : "omaskitchen", token, true);

    private static BusinessChannel Card(ChannelsState state) =>
        state.Workspace.Channels.Single(c => c.Id == ChannelId.Instagram);

    [Fact]
    public void AVerifiedAccountWithItsToken_IsConnected_UnderItsUsername()
    {
        ChannelsState state = new();
        state.SetInstagram(Setup());

        Assert.Equal(ChannelStatus.Connected, Card(state).Status);
        Assert.Equal("@omaskitchen", Card(state).AccountName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ASetupStillInProgress_IsNotConnected_AndReopensAtItsStep(bool verified, bool token)
    {
        ChannelsState state = new();
        state.SetInstagram(Setup(verified, token, account: token ? "17841400000000000" : null));

        BusinessChannel card = Card(state);
        Assert.Equal(ChannelStatus.NotConnected, card.Status);
        if (verified)
        {
            Assert.Equal(ChannelSetupMode.Guided, card.Setup.Mode);
            Assert.Equal(ManualSetupStage.Credentials, card.Setup.ManualStage);
        }
    }

    [Fact]
    public void Disconnecting_TakesTheCardOffline_UntilAFreshSetupLoads()
    {
        ChannelsState state = new();
        state.SetInstagram(Setup());

        state.DisconnectChannel("instagram");
        Assert.Equal(ChannelStatus.NotConnected, Card(state).Status);

        state.SetInstagram(Setup());
        Assert.Equal(ChannelStatus.Connected, Card(state).Status);
    }
}
