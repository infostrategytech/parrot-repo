using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Api.Media;
using Parrot.Application.Storage;
using Parrot.Infrastructure.Storage;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// The links in MessagePersisted: made by the message worker, opened by the API, for a week.
public class MediaLinkSignerTests
{
    private const string Key = "customer-media/user-1/wamid.1-0.jpg";
    private const string Prefix = "https://api.example.com/api/media/";

    private sealed class ListLogger : ILogger<MediaLinkSigner>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // A key ring the worker cannot seal with, e.g. one with no current key it may use.
    private sealed class BrokenProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => throw new CryptographicException("The key ring has no default key.");

        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("The key ring has no default key.");
    }

    private static MediaLinkSigner Signer(IDataProtectionProvider provider, string baseUrl = "https://api.example.com", ILogger<MediaLinkSigner>? logger = null) =>
        new(provider, Options.Create(new StorageSettings { MediaLinkBaseUrl = baseUrl }), logger ?? NullLogger<MediaLinkSigner>.Instance);

    [Fact]
    public void Sign_MakesALinkTheApisMediaEndpointOpens()
    {
        EphemeralDataProtectionProvider keyRing = new();

        string link = Signer(keyRing).Sign(Key, "image-20261123-132153.jpg")!;

        Assert.StartsWith(Prefix, link);
        Assert.True(new MediaLinks(keyRing).TryRead(link[Prefix.Length..], out string key, out string? fileName));
        Assert.Equal(Key, key);
        Assert.Equal("image-20261123-132153.jpg", fileName);
    }

    [Fact]
    public void Sign_TheLinkLastsAWeek()
    {
        EphemeralDataProtectionProvider keyRing = new();

        string token = Signer(keyRing).Sign(Key, "a.jpg")![Prefix.Length..];
        keyRing.CreateProtector(MediaLinkSigner.Purpose).ToTimeLimitedDataProtector().Unprotect(token, out DateTimeOffset expires);

        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public void Sign_ALinkMadeWithAnotherKeyRing_DoesNotOpen()
    {
        string token = Signer(new EphemeralDataProtectionProvider()).Sign(Key, "a.jpg")![Prefix.Length..];

        Assert.False(new MediaLinks(new EphemeralDataProtectionProvider()).TryRead(token, out _, out _));
    }

    [Fact]
    public void Sign_ATrailingSlashOnTheAddress_IsDropped() =>
        Assert.StartsWith(Prefix, Signer(new EphemeralDataProtectionProvider(), "https://api.example.com/").Sign(Key, "a.jpg"));

    [Theory]
    [InlineData("")]
    [InlineData("api.example.com")]
    [InlineData("/relative")]
    [InlineData("ftp://api.example.com")]
    [InlineData("https://api.example.com?x=1")]
    [InlineData("https://user:secret@api.example.com")]
    public void Sign_WithoutAUsableAddress_MakesNoLinkAndSaysSo(string baseUrl)
    {
        ListLogger logger = new();

        Assert.Null(Signer(new EphemeralDataProtectionProvider(), baseUrl, logger).Sign(Key, "a.jpg"));
        Assert.Contains(logger.Warnings, w => w.StartsWith("media_links.disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Sign_WhenSealingFails_MakesNoLinkAndLogsAWarning()
    {
        ListLogger logger = new();

        Assert.Null(Signer(new BrokenProtector(), logger: logger).Sign(Key, "a.jpg"));
        Assert.Contains(logger.Warnings, w => w.StartsWith("media_links.sign_failed", StringComparison.Ordinal));
    }
}
