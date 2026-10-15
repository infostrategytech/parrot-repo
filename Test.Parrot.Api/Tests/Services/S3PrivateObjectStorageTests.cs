using Microsoft.Extensions.Options;
using Parrot.Application.Storage;
using Parrot.Infrastructure.Storage;

namespace Test.Parrot.Api.Tests.Services;

// The streaming paths on a real S3-compatible service (MinIO or S3), which the fakes cannot prove: a stream of
// unknown length is uploaded in parts, a failing stream leaves nothing behind, and a range reads only its bytes.
// Runs only when PARROT_TEST_S3_URL, PARROT_TEST_S3_ACCESS, PARROT_TEST_S3_SECRET and PARROT_TEST_S3_BUCKET are
// set; the bucket must exist. Objects are written under a test/ prefix and left there (the largest is 12 MB).
public class S3PrivateObjectStorageTests
{
    private static S3PrivateObjectStorage? Storage()
    {
        string? url = Environment.GetEnvironmentVariable("PARROT_TEST_S3_URL");
        string? access = Environment.GetEnvironmentVariable("PARROT_TEST_S3_ACCESS");
        string? secret = Environment.GetEnvironmentVariable("PARROT_TEST_S3_SECRET");
        string? bucket = Environment.GetEnvironmentVariable("PARROT_TEST_S3_BUCKET");
        if (url is null || access is null || secret is null || bucket is null) return null;
        return new S3PrivateObjectStorage(Options.Create(new StorageSettings { ServiceUrl = url, AccessKey = access, SecretKey = secret, MediaBucket = bucket }));
    }

    private static string NewKey() => $"test/{Guid.NewGuid():N}.bin";

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    // Reads like a download: no length, no seeking, a little at a time.
    private sealed class Trickle(byte[] bytes, int failAt = -1) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAt >= 0 && _position >= failAt) throw new IOException("The download broke.");
            int n = Math.Min(Math.Min(count, 64 * 1024), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<byte[]> ReadAsync(StoredObject? stored)
    {
        Assert.NotNull(stored);
        using (stored)
        {
            using MemoryStream copy = new();
            await stored!.Content.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(12_000_000)]
    public async Task A_stream_of_unknown_length_is_stored_whole_whether_it_fits_one_part_or_needs_several(int size)
    {
        if (Storage() is not { } storage) return;
        string key = NewKey();
        byte[] body = Pattern(size);

        await storage.PutAsync(key, new Trickle(body), "video/mp4");

        StoredObjectInfo? info = await storage.StatAsync(key);
        Assert.Equal(size, info!.Length);
        Assert.Equal("video/mp4", info.ContentType);
        Assert.Equal(body, await ReadAsync(await storage.GetAsync(key)));
    }

    [Fact]
    public async Task A_stream_that_breaks_part_way_leaves_no_object_behind()
    {
        if (Storage() is not { } storage) return;
        string key = NewKey();

        await Assert.ThrowsAnyAsync<Exception>(() => storage.PutAsync(key, new Trickle(Pattern(12_000_000), failAt: 7_000_000), "video/mp4"));

        Assert.Null(await storage.StatAsync(key));
    }

    [Fact]
    public async Task A_range_returns_only_its_bytes_and_a_missing_object_is_null()
    {
        if (Storage() is not { } storage) return;
        string key = NewKey();
        byte[] body = Pattern(5_000);
        await storage.PutAsync(key, new MemoryStream(body), "application/pdf");

        Assert.Equal(body[1_000..1_500], await ReadAsync(await storage.GetRangeAsync(key, 1_000, 1_499)));
        Assert.Null(await storage.StatAsync(NewKey()));
        Assert.Null(await storage.GetRangeAsync(NewKey(), 0, 10));
    }
}
