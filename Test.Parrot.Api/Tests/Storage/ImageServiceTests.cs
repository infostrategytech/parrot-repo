using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Parrot.Application.Storage;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Storage;

public class ImageServiceTests
{
    private const string UserId = "3f6a4d8e-3a67-4d4f-9d43-1b2f6b7a9c11";

    private readonly RecordingStorage _storage = new();
    private readonly ImageService _sut;

    public ImageServiceTests()
    {
        _sut = new ImageService(_storage, Options.Create(new StorageSettings
        {
            Bucket = "parrot",
            PublicBaseUrl = "https://files.test/parrot",
            MaxImageBytes = 1024,
        }));
    }

    [Fact]
    public async Task SaveUserImageAsync_ReturnsTheUrlTheStorageGaveBack()
    {
        string url = await Save("image/png");

        Assert.Equal($"https://files.test/parrot/{_storage.Key}", url);
    }

    [Fact]
    public async Task SaveUserImageAsync_ForwardsTheContentTypeUnchanged()
    {
        await Save("image/webp");

        Assert.Equal("image/webp", _storage.ContentType);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    public async Task SaveUserImageAsync_KeysTheObjectFromTheContentTypeAndAGuid(string contentType, string extension)
    {
        await Save(contentType);

        Assert.NotNull(_storage.Key);
        Assert.Matches(
            $"^users/{UserId}/[0-9a-f]{{32}}{Regex.Escape(extension)}$",
            _storage.Key);
    }

    [Fact]
    public async Task SaveUserImageAsync_WhenTheFileWasNeverSent_Throws()
    {
        ValidationException error = await Assert.ThrowsAsync<ValidationException>(
            () => _sut.SaveUserImageAsync(UserId, Stream.Null, string.Empty, 0));

        Assert.Equal("Choose an image to upload.", error.Message);
    }

    [Fact]
    public async Task SaveUserImageAsync_WhenTheImageIsOverTheLimit_Throws()
    {
        ValidationException error = await Assert.ThrowsAsync<ValidationException>(
            () => _sut.SaveUserImageAsync(UserId, Stream.Null, "image/png", 1025));

        Assert.Equal("Choose an image no larger than 1 KB.", error.Message);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("")]
    public async Task SaveUserImageAsync_WhenTheContentTypeIsNotAccepted_Throws(string contentType)
    {
        ValidationException error = await Assert.ThrowsAsync<ValidationException>(
            () => _sut.SaveUserImageAsync(UserId, Stream.Null, contentType, 10));

        Assert.Equal("Choose a PNG, JPEG, GIF or WebP image.", error.Message);
    }

    [Fact]
    public async Task SaveUserImageAsync_NeverPlacesAThrownErrorUnderTheUsersFolder()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _sut.SaveUserImageAsync(UserId, Stream.Null, "text/html", 10));

        Assert.Null(_storage.Key);
    }

    [Theory]
    [InlineData("../etc/passwd", "etcpasswd")]
    [InlineData("../../seun", "seun")]
    [InlineData("a b/c", "abc")]
    public async Task SaveUserImageAsync_KeepsTheAccountScopeToSafeCharacters(string userId, string expectedScope)
    {
        await _sut.SaveUserImageAsync(userId, Stream.Null, "image/png", 10);

        Assert.NotNull(_storage.Key);
        Assert.StartsWith($"users/{expectedScope}/", _storage.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("///")]
    [InlineData("....")]
    public async Task SaveUserImageAsync_WhenNothingSafeIsLeftOfTheAccount_StillKeysItSomewhere(string userId)
    {
        await _sut.SaveUserImageAsync(userId, Stream.Null, "image/png", 10);

        Assert.NotNull(_storage.Key);
        Assert.Matches("^users/[0-9a-f]{32}/[0-9a-f]{32}\\.png$", _storage.Key);
    }

    private Task<string> Save(string contentType) =>
        _sut.SaveUserImageAsync(UserId, Stream.Null, contentType, 10);

    private sealed class RecordingStorage : IObjectStorage
    {
        public string? Key { get; private set; }

        public string? ContentType { get; private set; }

        public Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Key = key;
            ContentType = contentType;
            return Task.FromResult($"https://files.test/parrot/{key}");
        }
    }
}
