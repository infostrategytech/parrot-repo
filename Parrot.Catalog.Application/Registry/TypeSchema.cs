using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Registry;

public enum AttributeScope
{
    Product,
    Variant,
}

/// <summary>
/// The compiled registry for one (product_type_id, type_version). It is the single
/// source for validation, the admin form schema, the derived index, the search text
/// and the canonical document, so none of those can disagree with each other.
/// </summary>
public sealed class TypeSchema
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<string, CompiledAttribute> _byKey;

    public TypeSchema(ProductType type, IEnumerable<AttributeDefinition> definitions)
    {
        ProductTypeId = type.Id;
        TypeKey = type.Key;
        TypeName = type.Name;
        Kind = type.Kind;
        TypeVersion = definitions.Select(d => d.TypeVersion).DefaultIfEmpty(type.Version).First();
        Attributes = definitions
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(CompiledAttribute.From)
            .ToArray();
        _byKey = Attributes.ToDictionary(a => a.Key, StringComparer.Ordinal);
    }

    public Guid ProductTypeId { get; }

    public string TypeKey { get; }

    public string TypeName { get; }

    public ProductTypeKind Kind { get; }

    public int TypeVersion { get; }

    public IReadOnlyList<CompiledAttribute> Attributes { get; }

    public IEnumerable<CompiledAttribute> For(AttributeScope scope)
        => Attributes.Where(a => a.VariantAxis == (scope == AttributeScope.Variant));

    public CompiledAttribute? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Validates a JSON object of attributes. Returns the errors; empty means valid.</summary>
    public IReadOnlyList<string> Validate(string attributesJson, AttributeScope scope)
    {
        List<string> errors = new List<string>();
        if (Encoding.UTF8.GetByteCount(attributesJson) > Limits.MaxAttributesBytes)
        {
            errors.Add($"attributes must be at most {Limits.MaxAttributesBytes} bytes.");
            return errors;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(attributesJson, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            errors.Add($"attributes is not valid JSON: {ex.Message}");
            return errors;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("attributes must be a JSON object.");
                return errors;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(prop.Name))
                {
                    errors.Add($"attributes.{prop.Name} appears more than once.");
                    continue;
                }

                CompiledAttribute? def = Find(prop.Name);
                if (def is null)
                {
                    errors.Add($"attributes.{prop.Name} is not defined for type '{TypeKey}' version {TypeVersion}.");
                    continue;
                }

                bool scopeOk = def.VariantAxis == (scope == AttributeScope.Variant);
                if (!scopeOk)
                {
                    errors.Add(def.VariantAxis
                        ? $"attributes.{prop.Name} is a variant-axis attribute and belongs on variants, not the product."
                        : $"attributes.{prop.Name} is a product attribute and cannot be set on a variant.");
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    if (def.Required)
                    {
                        errors.Add($"attributes.{prop.Name} is required.");
                    }

                    continue;
                }

                ValidateValue(def, prop.Value, errors);
            }

            foreach (CompiledAttribute def in For(scope))
            {
                if (def.Required && !seen.Contains(def.Key))
                {
                    errors.Add($"attributes.{def.Key} is required.");
                }
            }
        }

        return errors;
    }

    /// <summary>JSON Schema (2020-12) plus x-ui hints for the given scope.</summary>
    public JsonObject ToJsonSchema(AttributeScope scope)
    {
        JsonObject properties = new JsonObject();
        JsonArray required = new JsonArray();
        foreach (CompiledAttribute a in For(scope))
        {
            properties[a.Key] = PropertySchema(a);
            if (a.Required)
            {
                required.Add(a.Key);
            }
        }

        JsonObject schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"urn:parrot:catalog:type:{TypeKey}:v{TypeVersion}:{EnumNames.ToSnake(scope.ToString())}",
            ["title"] = scope == AttributeScope.Product ? TypeName : $"{TypeName} variant",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["x-type"] = new JsonObject
            {
                ["key"] = TypeKey,
                ["kind"] = EnumNames.ToSnake(Kind),
                ["version"] = TypeVersion,
            },
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject PropertySchema(CompiledAttribute a)
    {
        JsonObject p = new JsonObject { ["title"] = a.Label };
        switch (a.DataType)
        {
            case AttributeDataType.String:
                p["type"] = "string";
                p["maxLength"] = a.Rules.MaxLength ?? 1024;
                break;
            case AttributeDataType.Text:
                p["type"] = "string";
                p["maxLength"] = a.Rules.MaxLength ?? Limits.MaxDescriptionLength;
                break;
            case AttributeDataType.Integer:
                p["type"] = "integer";
                break;
            case AttributeDataType.Number:
                p["type"] = "number";
                break;
            case AttributeDataType.Boolean:
                p["type"] = "boolean";
                break;
            case AttributeDataType.Date:
                p["type"] = "string";
                p["format"] = "date";
                break;
            case AttributeDataType.Enum:
                p["type"] = "string";
                p["enum"] = new JsonArray(a.EnumOptions.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
                break;
            case AttributeDataType.MultiEnum:
                p["type"] = "array";
                p["uniqueItems"] = true;
                p["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(a.EnumOptions.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
                };
                break;
            case AttributeDataType.Json:
                p["type"] = new JsonArray("object", "array");
                break;
        }

        if (a.Rules.Min.HasValue && a.DataType is AttributeDataType.Integer or AttributeDataType.Number)
        {
            p["minimum"] = a.Rules.Min.Value;
        }

        if (a.Rules.Max.HasValue && a.DataType is AttributeDataType.Integer or AttributeDataType.Number)
        {
            p["maximum"] = a.Rules.Max.Value;
        }

        if (a.Rules.Regex is not null && a.DataType is AttributeDataType.String or AttributeDataType.Text)
        {
            p["pattern"] = a.Rules.Regex;
        }

        if (a.Rules.MaxLength.HasValue && a.DataType is AttributeDataType.String or AttributeDataType.Text)
        {
            p["maxLength"] = a.Rules.MaxLength.Value;
        }

        p["x-ui"] = new JsonObject
        {
            ["unit"] = a.Unit,
            ["display_group"] = a.DisplayGroup,
            ["sort_order"] = a.SortOrder,
            ["filterable"] = a.Filterable,
            ["searchable"] = a.Searchable,
            ["embeddable"] = a.Embeddable,
            ["variant_axis"] = a.VariantAxis,
            ["data_type"] = EnumNames.ToSnake(a.DataType),
        };
        return p;
    }

    private static void ValidateValue(CompiledAttribute def, JsonElement value, List<string> errors)
    {
        string path = $"attributes.{def.Key}";
        switch (def.DataType)
        {
            case AttributeDataType.String:
            case AttributeDataType.Text:
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{path} must be a string.");
                    return;
                }

                string s = value.GetString()!;
                int maxLen = def.Rules.MaxLength ?? (def.DataType == AttributeDataType.String ? 1024 : Limits.MaxDescriptionLength);
                if (s.Length > maxLen)
                {
                    errors.Add($"{path} must be at most {maxLen} characters.");
                }

                if (Guards.HasControlCharacters(s, allowNewlines: def.DataType == AttributeDataType.Text))
                {
                    errors.Add($"{path} must not contain control characters.");
                }

                if (def.Rules.Regex is not null && !SafeRegexMatch(def.Rules.Regex, s))
                {
                    errors.Add($"{path} does not match the required pattern.");
                }

                return;

            case AttributeDataType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long l))
                {
                    errors.Add($"{path} must be an integer.");
                    return;
                }

                CheckBounds(def, l, path, errors);
                return;

            case AttributeDataType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal d))
                {
                    errors.Add($"{path} must be a number.");
                    return;
                }

                CheckBounds(def, d, path, errors);
                return;

            case AttributeDataType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add($"{path} must be true or false.");
                }

                return;

            case AttributeDataType.Date:
                if (value.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add($"{path} must be a date in the form YYYY-MM-DD.");
                }

                return;

            case AttributeDataType.Enum:
                if (value.ValueKind != JsonValueKind.String || !def.EnumOptions.Contains(value.GetString()!, StringComparer.Ordinal))
                {
                    errors.Add($"{path} must be one of: {string.Join(", ", def.EnumOptions)}.");
                }

                return;

            case AttributeDataType.MultiEnum:
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add($"{path} must be an array.");
                    return;
                }

                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !def.EnumOptions.Contains(item.GetString()!, StringComparer.Ordinal))
                    {
                        errors.Add($"{path} items must be one of: {string.Join(", ", def.EnumOptions)}.");
                        return;
                    }

                    if (!seen.Add(item.GetString()!))
                    {
                        errors.Add($"{path} must not contain duplicates.");
                        return;
                    }
                }

                return;

            case AttributeDataType.Json:
                if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    errors.Add($"{path} must be a JSON object or array.");
                }

                return;
        }
    }

    private static void CheckBounds(CompiledAttribute def, decimal actual, string path, List<string> errors)
    {
        if (def.Rules.Min.HasValue && actual < def.Rules.Min.Value)
        {
            errors.Add($"{path} must be at least {def.Rules.Min.Value}.");
        }

        if (def.Rules.Max.HasValue && actual > def.Rules.Max.Value)
        {
            errors.Add($"{path} must be at most {def.Rules.Max.Value}.");
        }
    }

    private static bool SafeRegexMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
