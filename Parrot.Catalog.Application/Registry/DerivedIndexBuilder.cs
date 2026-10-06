using System.Globalization;
using System.Text;
using System.Text.Json;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Registry;

/// <summary>
/// Turns validated attributes into product_attribute_index rows and search text.
/// Pure: the caller runs it inside the writing transaction (ADR-002).
/// </summary>
public static class DerivedIndexBuilder
{
    public static IReadOnlyList<ProductAttributeIndex> BuildRows(
        TypeSchema schema,
        Guid tenantId,
        Guid catalogId,
        Guid productId,
        Guid? variantId,
        string attributesJson)
    {
        List<ProductAttributeIndex> rows = new List<ProductAttributeIndex>();
        using JsonDocument doc = JsonDocument.Parse(attributesJson);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            CompiledAttribute? def = schema.Find(prop.Name);
            if (def is null || !def.Filterable || prop.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            foreach (ProductAttributeIndex row in RowsFor(def, prop.Value))
            {
                row.TenantId = tenantId;
                row.CatalogId = catalogId;
                row.ProductId = productId;
                row.VariantId = variantId;
                row.AttributeKey = def.Key;
                rows.Add(row);
            }
        }

        return rows;
    }

    public static string BuildSearchText(TypeSchema schema, string name, string sku, string? description, string attributesJson, IEnumerable<string> variantAttributeJsons)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(name).Append(' ').Append(sku);
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(' ').Append(description);
        }

        AppendSearchable(schema, attributesJson, sb);
        foreach (string variantJson in variantAttributeJsons)
        {
            AppendSearchable(schema, variantJson, sb);
        }

        return sb.ToString();
    }

    private static void AppendSearchable(TypeSchema schema, string json, StringBuilder sb)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            CompiledAttribute? def = schema.Find(prop.Name);
            if (def is null || !def.Searchable || prop.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    sb.Append(' ').Append(prop.Value.GetString());
                    break;
                case JsonValueKind.Number:
                    sb.Append(' ').Append(prop.Value.GetRawText());
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(' ').Append(item.GetString());
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<ProductAttributeIndex> RowsFor(CompiledAttribute def, JsonElement value)
    {
        switch (def.DataType)
        {
            case AttributeDataType.Integer:
            case AttributeDataType.Number:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal d))
                {
                    yield return new ProductAttributeIndex { NumericValue = d };
                }

                break;
            case AttributeDataType.String:
            case AttributeDataType.Enum:
                if (value.ValueKind == JsonValueKind.String)
                {
                    yield return new ProductAttributeIndex { TextValue = value.GetString() };
                }

                break;
            case AttributeDataType.MultiEnum:
                if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            yield return new ProductAttributeIndex { TextValue = item.GetString() };
                        }
                    }
                }

                break;
            case AttributeDataType.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    yield return new ProductAttributeIndex { BoolValue = value.GetBoolean() };
                }

                break;
            case AttributeDataType.Date:
                if (value.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    yield return new ProductAttributeIndex { DateValue = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) };
                }

                break;
        }
    }
}
