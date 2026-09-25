using Parrot.Application.Integrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class ProviderTimestampTests
{
    // 2025-09-24T09:20:00Z in each platform's own unit.
    private const long Seconds = 1758705600;
    private const long Milliseconds = 1758705600000;

    [Fact]
    public void BothPlatformsUnitsNormaliseToTheSameInstant()
    {
        DateTime? whatsApp = ProviderTimestamp.FromUnixSeconds(Seconds.ToString());
        DateTime? messenger = ProviderTimestamp.FromUnixMilliseconds(Milliseconds);

        Assert.NotNull(whatsApp);
        Assert.Equal(whatsApp, messenger);
        Assert.Equal(DateTimeKind.Utc, whatsApp!.Value.Kind);
    }

    [Fact]
    public void MillisecondsReadAsSecondsWouldLandMillenniaAway()
    {
        // The bug this guards: the raw Messenger value parsed as seconds is far outside any sane range,
        // so it is rejected rather than stored or compared.
        Assert.Null(ProviderTimestamp.FromUnixSeconds(Milliseconds.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    public void UnusableProviderValuesBecomeNullRatherThanAWrongDate(string? value)
    {
        Assert.Null(ProviderTimestamp.FromUnixSeconds(value));
    }

    [Fact]
    public void ASmallClockSkewIsToleratedButAFarFutureValueIsNot()
    {
        long soon = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        Assert.NotNull(ProviderTimestamp.FromUnixSeconds(soon.ToString()));

        long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        Assert.Null(ProviderTimestamp.FromUnixSeconds(farFuture.ToString()));
    }
}
