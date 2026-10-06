using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Catalog.Application.Validation;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>JSON helpers: one wire format (snake_case, enums as snake_case strings) and strict object normalisation.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Wire = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Wire);

    public static JsonElement Element(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement? ElementOrNull(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : Element(json);

    /// <summary>Returns a compact JSON string for an object-valued element; <c>{}</c> for null; rejects anything else or anything too large.</summary>
    public static string ObjectText(JsonElement? element, string field, int maxBytes = Limits.MaxAttributesBytes)
    {
        if (element is null || element.Value.ValueKind == JsonValueKind.Undefined || element.Value.ValueKind == JsonValueKind.Null)
        {
            return "{}";
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationException($"{field} must be a JSON object.");
        }

        string text = element.Value.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
        {
            throw new ValidationException($"{field} must be at most {maxBytes} bytes.");
        }

        return text;
    }

    public static string ArrayText(IReadOnlyList<string>? values)
        => values is null ? "[]" : JsonSerializer.Serialize(values, Wire);
}
