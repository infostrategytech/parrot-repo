using System.Collections.Concurrent;
using Parrot.Catalog.Application.Services;

namespace Test.Parrot.Catalog.Fixtures;

/// <summary>Items' photos kept in memory, so tests can see what was stored and what was removed.</summary>
public sealed class FakePhotoStorage : IItemPhotoStorage
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _files = new(StringComparer.Ordinal);

    public bool IsConfigured => true;

    public bool Has(string key) => _files.ContainsKey(key);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using MemoryStream copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        _files[key] = (copy.ToArray(), contentType);
    }

    public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        foreach (string key in keys)
        {
            _files.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public string UrlFor(string key) => $"https://photos.test/{key}";
}
