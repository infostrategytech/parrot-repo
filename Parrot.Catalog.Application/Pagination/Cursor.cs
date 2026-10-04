using System.Text;
using System.Text.Json;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Application.Pagination;

/// <summary>
/// Opaque keyset cursor: the sort key, the last row's sort value and id. Base64url of
/// compact JSON; decoding is strict and the sort key must match the request's.
/// </summary>
public sealed record Cursor(string Sort, string? Value, Guid Id)
{
    private const int MaxLength = 512;

    public string Encode()
    {
        string json = JsonSerializer.Serialize(new CursorWire(Sort, Value, Id.ToString("N")));
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static Cursor? Decode(string? encoded, string expectedSort)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        if (encoded.Length > MaxLength)
        {
            throw new ValidationException("cursor is not valid.");
        }

        try
        {
            byte[] bytes = Base64UrlDecode(encoded);
            CursorWire? wire = JsonSerializer.Deserialize<CursorWire>(bytes);
            if (wire is null || wire.S is null || wire.I is null || !Guid.TryParseExact(wire.I, "N", out Guid id))
            {
                throw new ValidationException("cursor is not valid.");
            }

            if (!string.Equals(wire.S, expectedSort, StringComparison.Ordinal))
            {
                throw new ValidationException("cursor does not belong to this sort order.");
            }

            return new Cursor(wire.S, wire.V, id);
        }
        catch (FormatException)
        {
            throw new ValidationException("cursor is not valid.");
        }
        catch (JsonException)
        {
            throw new ValidationException("cursor is not valid.");
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record CursorWire(string? S, string? V, string? I);
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class PageSize
{
    public static int Clamp(int? requested, int max, int fallback)
    {
        if (requested is null)
        {
            return fallback;
        }

        if (requested.Value < 1 || requested.Value > max)
        {
            throw new ValidationException($"limit must be between 1 and {max}.");
        }

        return requested.Value;
    }
}
