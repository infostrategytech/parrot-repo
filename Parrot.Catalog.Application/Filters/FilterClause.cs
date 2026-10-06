using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Catalog.Application.Filters;

/// <summary>
/// One entry of the filter DSL: <c>{ "ram_gb": { "gte": 16 } }</c>. Exactly the five
/// operators from the spec; anything else fails model binding because unknown members
/// are rejected globally.
/// </summary>
public sealed record FilterClause
{
    [JsonPropertyName("eq")]
    public JsonElement? Eq { get; init; }

    [JsonPropertyName("in")]
    public JsonElement? In { get; init; }

    [JsonPropertyName("gte")]
    public JsonElement? Gte { get; init; }

    [JsonPropertyName("lte")]
    public JsonElement? Lte { get; init; }

    [JsonPropertyName("between")]
    public JsonElement? Between { get; init; }

    public bool IsEmpty => Eq is null && In is null && Gte is null && Lte is null && Between is null;
}
