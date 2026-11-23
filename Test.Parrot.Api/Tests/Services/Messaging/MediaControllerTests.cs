using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Api.Controllers;
using Parrot.Api.Media;
using Parrot.Application.Conversations;
using Parrot.Application.Storage;
using Parrot.Infrastructure.Storage;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class MediaControllerTests
{
    private const string Key = "customer-media/user-1/a-0.mp4";
    private static readonly byte[] Bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    private sealed class Storage(string contentType = "video/mp4") : IPrivateObjectStorage
    {
        public List<(long Start, long End)> Ranges { get; } = new();
        public int Whole { get; private set; }

        public Task PutAsync(string key, Stream content, string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StoredObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == Key ? new StoredObjectInfo(contentType, Bytes.Length) : null);

        public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            Whole++;
            return Task.FromResult(key == Key ? new StoredObject(new MemoryStream(Bytes), contentType, Bytes.Length) : null);
        }

        public Task<StoredObject?> GetRangeAsync(string key, long start, long end, CancellationToken cancellationToken = default)
        {
            Ranges.Add((start, end));
            return Task.FromResult(key == Key
                ? new StoredObject(new MemoryStream(Bytes[(int)start..((int)end + 1)]), contentType, end - start + 1)
                : null);
        }
    }

    // One key ring, as the API and the message worker share one.
    private static readonly EphemeralDataProtectionProvider KeyRing = new();

    private static readonly MediaLinks Links = new(KeyRing);

    private static string Token(string key = Key) => Links.Sign(new MessageMedia("video", "video/mp4", "clip.mp4", 100, null) { StorageKey = key }).Url!["api/media/".Length..];

    private static async Task<DefaultHttpContext> GetAsync(Storage storage, string token, string? range = null, bool download = false)
    {
        MediaController controller = new(Links, storage);
        // The services MVC's own results look up when they run.
        ServiceCollection services = new();
        services.AddLogging();
        services.AddControllers();
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (range is not null) http.Request.Headers.Range = range;
        controller.ControllerContext = new ControllerContext(new ActionContext(http, new RouteData(), new ControllerActionDescriptor()));

        IActionResult result = await controller.Get(token, download, default);
        await result.ExecuteResultAsync(controller.ControllerContext);
        return http;
    }

    private static byte[] Body(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        using MemoryStream copy = new();
        http.Response.Body.CopyTo(copy);
        return copy.ToArray();
    }

    // The link in MessagePersisted is the only credential the AI team has: no sign-in, just the address.
    [Fact]
    public async Task A_week_long_link_from_the_persisted_event_downloads_without_signing_in()
    {
        MediaLinkSigner signer = new(KeyRing, Options.Create(new StorageSettings { MediaLinkBaseUrl = "https://api.example.com" }),
            NullLogger<MediaLinkSigner>.Instance);
        string token = signer.Sign(Key, "clip.mp4")!["https://api.example.com/api/media/".Length..];

        DefaultHttpContext http = await GetAsync(new Storage(), token, download: true);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(Bytes, Body(http));
        Assert.Contains("clip.mp4", http.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Without_a_range_the_whole_file_is_streamed_with_its_length_and_range_support_announced()
    {
        Storage storage = new();

        DefaultHttpContext http = await GetAsync(storage, Token());

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(Bytes, Body(http));
        Assert.Equal(100, http.Response.ContentLength);
        Assert.Equal("bytes", http.Response.Headers.AcceptRanges);
        Assert.Equal("video/mp4", http.Response.ContentType);
        Assert.Empty(storage.Ranges);
    }

    [Theory]
    [InlineData("bytes=10-19", 10, 19)]
    [InlineData("bytes=90-", 90, 99)]
    [InlineData("bytes=95-500", 95, 99)]
    [InlineData("bytes=-5", 95, 99)]
    [InlineData("bytes=-500", 0, 99)]
    [InlineData("bytes=0-0", 0, 0)]
    public async Task A_range_fetches_only_those_bytes_from_storage_and_answers_206(string header, long start, long end)
    {
        Storage storage = new();

        DefaultHttpContext http = await GetAsync(storage, Token(), header);

        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal(Bytes[(int)start..((int)end + 1)], Body(http));
        Assert.Equal($"bytes {start}-{end}/100", http.Response.Headers.ContentRange);
        Assert.Equal(end - start + 1, http.Response.ContentLength);
        Assert.Equal([(start, end)], storage.Ranges);
        Assert.Equal(0, storage.Whole);
    }

    [Theory]
    [InlineData("bytes=100-")]
    [InlineData("bytes=250-300")]
    [InlineData("bytes=-0")]
    public async Task A_range_past_the_end_is_answered_416_with_the_size(string header)
    {
        Storage storage = new();

        DefaultHttpContext http = await GetAsync(storage, Token(), header);

        Assert.Equal(416, http.Response.StatusCode);
        Assert.Equal("bytes */100", http.Response.Headers.ContentRange);
        Assert.Empty(storage.Ranges);
    }

    [Theory]
    [InlineData("bytes=5-2")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("items=0-5")]
    public async Task A_range_that_is_not_one_valid_byte_range_is_ignored_and_the_whole_file_served(string header)
    {
        DefaultHttpContext http = await GetAsync(new Storage(), Token(), header);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(Bytes, Body(http));
    }

    [Fact]
    public async Task A_forged_token_or_a_missing_object_is_a_404_and_reads_nothing()
    {
        Storage storage = new();

        Assert.Equal(404, (await GetAsync(storage, "forged")).Response.StatusCode);
        Assert.Equal(404, (await GetAsync(storage, Token("customer-media/user-1/gone.mp4"))).Response.StatusCode);
        Assert.Equal(0, storage.Whole);
        Assert.Empty(storage.Ranges);
    }

    [Fact]
    public async Task A_type_the_browser_must_not_draw_downloads_as_an_opaque_file()
    {
        DefaultHttpContext http = await GetAsync(new Storage("text/html"), Token());

        Assert.Equal("application/octet-stream", http.Response.ContentType);
        Assert.StartsWith("attachment", http.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("nosniff", http.Response.Headers["X-Content-Type-Options"]);
    }

    // ---- Memory under load: many large files at once must not be held in memory ----

    // Produces its bytes on demand, so no test ever holds a large file either.
    private sealed class Generated(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _position);
            Array.Fill(buffer, (byte)7, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HugeStorage(long size) : IPrivateObjectStorage
    {
        public Task PutAsync(string key, Stream content, string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<StoredObjectInfo?>(new("video/mp4", size));
        public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<StoredObject?>(new(new Generated(size), "video/mp4", size));
        public Task<StoredObject?> GetRangeAsync(string key, long start, long end, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredObject?>(new(new Generated(end - start + 1), "video/mp4", end - start + 1));
    }

    [Fact]
    public async Task Fifty_concurrent_downloads_of_a_100_MB_file_move_5_GB_through_without_allocating_anything_like_it()
    {
        const long size = 100L * 1024 * 1024;
        const int concurrent = 50;
        string token = Token();
        long sent = 0;
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);

        await Task.WhenAll(Enumerable.Range(0, concurrent).Select(async _ =>
        {
            MediaController controller = new(Links, new HugeStorage(size));
            ServiceCollection services = new();
            services.AddLogging();
            services.AddControllers();
            DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
            CountingSink sink = new();
            http.Response.Body = sink;
            controller.ControllerContext = new ControllerContext(new ActionContext(http, new RouteData(), new ControllerActionDescriptor()));
            IActionResult result = await controller.Get(token, false, default);
            await result.ExecuteResultAsync(controller.ControllerContext);
            Interlocked.Add(ref sent, sink.Written);
        }));

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(size * concurrent, sent);
        // Buffering would allocate at least the 5 GB; streaming allocates buffers and request objects only.
        Assert.True(allocated < 200L * 1024 * 1024, $"allocated {allocated / 1024 / 1024} MB moving {sent / 1024 / 1024} MB");
    }

    private sealed class CountingSink : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Written += buffer.Length; return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Written += count; return Task.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
