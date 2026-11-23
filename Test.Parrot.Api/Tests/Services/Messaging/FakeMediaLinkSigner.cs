using Parrot.Application.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

/// <summary>A readable stand-in for a download link, so a test can see which file and name it was made for.</summary>
public sealed class FakeMediaLinkSigner(bool isConfigured = true) : IMediaLinkSigner
{
    public static string LinkFor(string storageKey, string fileName) => $"https://api.test/api/media/{storageKey}|{fileName}";

    public string? Sign(string storageKey, string fileName) => isConfigured ? LinkFor(storageKey, fileName) : null;
}
