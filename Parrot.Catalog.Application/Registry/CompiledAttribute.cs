using System.Text.Json;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Registry;

public sealed record AttributeValidationRules(
    decimal? Min,
    decimal? Max,
    string? Regex,
    int? MaxLength)
{
    public static readonly AttributeValidationRules None = new(null, null, null, null);

    public static AttributeValidationRules Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        decimal? min = root.TryGetProperty("min", out JsonElement minEl) && minEl.ValueKind == JsonValueKind.Number ? minEl.GetDecimal() : null;
        decimal? max = root.TryGetProperty("max", out JsonElement maxEl) && maxEl.ValueKind == JsonValueKind.Number ? maxEl.GetDecimal() : null;
        string? regex = root.TryGetProperty("regex", out JsonElement reEl) && reEl.ValueKind == JsonValueKind.String ? reEl.GetString() : null;
        int? maxLength = root.TryGetProperty("max_length", out JsonElement mlEl) && mlEl.ValueKind == JsonValueKind.Number && mlEl.TryGetInt32(out int ml) ? ml : null;
        return new AttributeValidationRules(min, max, regex, maxLength);
    }
}

/// <summary>One attribute_definitions row, parsed once and shared by every validation.</summary>
public sealed record CompiledAttribute(
    Guid DefinitionId,
    string Key,
    string Label,
    AttributeDataType DataType,
    string? Unit,
    bool Required,
    IReadOnlyList<string> EnumOptions,
    bool Filterable,
    bool Searchable,
    bool Embeddable,
    bool VariantAxis,
    string? DisplayGroup,
    int SortOrder,
    AttributeValidationRules Rules)
{
    public static CompiledAttribute From(AttributeDefinition d)
    {
        IReadOnlyList<string> options = [];
        if (!string.IsNullOrWhiteSpace(d.EnumOptions))
        {
            using JsonDocument doc = JsonDocument.Parse(d.EnumOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                options = doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }
        }

        return new CompiledAttribute(
            d.Id,
            d.Key,
            d.Label,
            d.DataType,
            d.Unit,
            d.Required,
            options,
            d.Filterable,
            d.Searchable,
            d.Embeddable,
            d.VariantAxis,
            d.DisplayGroup,
            d.SortOrder,
            AttributeValidationRules.Parse(d.Validation));
    }

    /// <summary>Which typed column of product_attribute_index this attribute's values land in.</summary>
    public IndexColumn IndexColumn => DataType switch
    {
        AttributeDataType.Integer or AttributeDataType.Number => IndexColumn.Numeric,
        AttributeDataType.String or AttributeDataType.Enum or AttributeDataType.MultiEnum => IndexColumn.Text,
        AttributeDataType.Boolean => IndexColumn.Bool,
        AttributeDataType.Date => IndexColumn.Date,
        _ => IndexColumn.None,
    };
}

public enum IndexColumn
{
    None,
    Numeric,
    Text,
    Bool,
    Date,
}
