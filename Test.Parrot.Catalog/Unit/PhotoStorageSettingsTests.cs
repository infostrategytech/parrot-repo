using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Storage;
using Xunit;

namespace Test.Parrot.Catalog.Unit;

/// <summary>The photo store says at start where photos go, and calls out a link base that would break every photo.</summary>
public sealed class PhotoStorageSettingsTests
{
    [Theory]
    [InlineData("https:///media")]
    [InlineData("https://media")]
    [InlineData("/media")]
    public void A_link_base_that_isnt_a_whole_address_is_warned_about(string publicBaseUrl)
    {
        Logs logs = new ();

        _ = new S3ItemPhotoStorage(Options(publicBaseUrl), logs);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("isn't a whole web address", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://parrot-public-1-us-west-1-an.s3.us-west-1.amazonaws.com")]
    [InlineData("https://api.example.com/media")]
    [InlineData("http://localhost:9000/parrot")]
    public void A_bucket_or_proxy_address_is_logged_without_a_warning(string publicBaseUrl)
    {
        Logs logs = new ();

        _ = new S3ItemPhotoStorage(Options(publicBaseUrl), logs);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(publicBaseUrl, StringComparison.Ordinal));
    }

    [Fact]
    public void No_link_base_says_uploads_are_off()
    {
        Logs logs = new ();

        _ = new S3ItemPhotoStorage(Options(""), logs);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("can't be uploaded", StringComparison.Ordinal));
    }

    private static IOptions<CatalogOptions> Options(string publicBaseUrl) => Microsoft.Extensions.Options.Options.Create(new CatalogOptions
    {
        Photos = new CatalogPhotoOptions { Bucket = "parrot", Region = "us-west-1", PublicBaseUrl = publicBaseUrl },
    });

    private sealed class Logs : ILogger<S3ItemPhotoStorage>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
