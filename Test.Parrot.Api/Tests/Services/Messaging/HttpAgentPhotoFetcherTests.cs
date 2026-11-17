using System.Net;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// Downloading the AI's photos: only public https links, only JPEG or PNG, and no more than 5 MB.
public sealed class HttpAgentPhotoFetcherTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2];

    private static HttpAgentPhotoFetcher Fetcher(HttpStatusCode status, byte[] body) =>
        new(new HttpClient(new Answer(status, body)));

    [Fact]
    public async Task A_jpeg_or_png_comes_back_named_and_typed_by_its_bytes()
    {
        var photo = await Fetcher(HttpStatusCode.OK, Jpeg).FetchAsync("https://cdn.example/tenants/t/items/abc123.jpg", default);
        Assert.Equal(("abc123.jpg", "image/jpeg", (long)Jpeg.Length), (photo.FileName, photo.ContentType, photo.Length));

        var png = await Fetcher(HttpStatusCode.OK, Png).FetchAsync("https://cdn.example/x/photo.jpg", default);
        Assert.Equal(("photo.png", "image/png"), (png.FileName, png.ContentType));
    }

    [Theory]
    [InlineData("http://cdn.example/a.jpg")]
    [InlineData("ftp://cdn.example/a.jpg")]
    [InlineData("not a link")]
    [InlineData("https://user:pass@cdn.example/a.jpg")]
    public async Task Anything_but_an_https_link_is_refused(string url)
    {
        await Assert.ThrowsAsync<AgentPhotoRejectedException>(() => Fetcher(HttpStatusCode.OK, Jpeg).FetchAsync(url, default));
    }

    [Fact]
    public async Task Not_found_not_an_image_or_too_big_is_refused()
    {
        await Assert.ThrowsAsync<AgentPhotoRejectedException>(() => Fetcher(HttpStatusCode.NotFound, Jpeg).FetchAsync("https://cdn.example/a.jpg", default));
        await Assert.ThrowsAsync<AgentPhotoRejectedException>(() => Fetcher(HttpStatusCode.OK, "<html>"u8.ToArray()).FetchAsync("https://cdn.example/a.jpg", default));
        byte[] huge = new byte[5 * 1024 * 1024 + 1];
        Jpeg.CopyTo(huge, 0);
        await Assert.ThrowsAsync<AgentPhotoRejectedException>(() => Fetcher(HttpStatusCode.OK, huge).FetchAsync("https://cdn.example/a.jpg", default));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("52.219.120.10", true)]
    [InlineData("2600:1f14::1", true)]
    public void Only_public_addresses_are_connected_to(string address, bool allowed)
    {
        Assert.Equal(allowed, HttpAgentPhotoFetcher.IsPublic(IPAddress.Parse(address)));
    }

    private sealed class Answer(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
