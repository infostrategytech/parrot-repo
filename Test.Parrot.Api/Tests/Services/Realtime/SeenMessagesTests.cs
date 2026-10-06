using Parrot.Web.Services.Realtime;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class SeenMessagesTests
{
    [Fact]
    public void TheFirstPushOfAMessageIsShown()
    {
        Assert.True(new SeenMessages().TryAdd("whatsapp:wamid.1"));
    }

    // The bus can deliver the same event twice; the client shows it once.
    [Fact]
    public void ARepeatedPushIsDropped()
    {
        SeenMessages seen = new();
        seen.TryAdd("whatsapp:wamid.1");

        Assert.False(seen.TryAdd("whatsapp:wamid.1"));
    }

    [Fact]
    public void DifferentMessagesAreAllShown()
    {
        SeenMessages seen = new();

        Assert.True(seen.TryAdd("whatsapp:wamid.1"));
        Assert.True(seen.TryAdd("whatsapp:wamid.2"));
    }

    // Memory stays bounded on a long-lived circuit: the oldest ids are forgotten.
    [Fact]
    public void OnlyTheMostRecentIdsAreKept()
    {
        SeenMessages seen = new(capacity: 2);
        seen.TryAdd("a");
        seen.TryAdd("b");
        seen.TryAdd("c");

        Assert.True(seen.TryAdd("a"));
        Assert.False(seen.TryAdd("c"));
    }
}
