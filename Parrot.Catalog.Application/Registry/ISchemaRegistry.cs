using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Application.Registry;

/// <summary>Compiles and caches a <see cref="TypeSchema"/> per (type, version) for the current tenant.</summary>
public interface ISchemaRegistry
{
    Task<TypeSchema> GetAsync(Guid productTypeId, int typeVersion, CancellationToken ct);

    /// <summary>Every filterable attribute across the tenant's types, keyed by attribute key.</summary>
    Task<IReadOnlyDictionary<string, FilterableAttribute>> GetFilterableAsync(Guid? productTypeId, CancellationToken ct);

    void Invalidate(Guid productTypeId);
}

/// <summary>What the filter compiler needs to know about a key: which typed column, and whether all types agree.</summary>
public sealed record FilterableAttribute(string Key, AttributeDataType DataType, IndexColumn Column, string Label, string? Unit, bool Ambiguous);

/// <summary>Rules a definition must satisfy before it joins the registry. Shared by create and version endpoints.</summary>
public static class AttributeDefinitionRules
{
    public static void Check(AttributeDefinition d)
    {
        Validation.Guards.RequireMatch(d.Key, Validation.Guards.AttributeKey(), "key", "must match ^[a-z][a-z0-9_]{0,63}$.");
        Validation.Guards.RequireText(d.Label, "label", Validation.Limits.MaxLabelLength);
        Validation.Guards.OptionalText(d.Unit, "unit", Validation.Limits.MaxUnitLength);
        Validation.Guards.OptionalText(d.DisplayGroup, "display_group", Validation.Limits.MaxLabelLength);

        if (d.Filterable && d.DataType is AttributeDataType.Text or AttributeDataType.Json)
        {
            throw new ValidationException($"attribute '{d.Key}': text and json attributes cannot be filterable.");
        }

        CompiledAttribute compiled = CompiledAttribute.From(d);
        if (d.DataType is AttributeDataType.Enum or AttributeDataType.MultiEnum)
        {
            if (compiled.EnumOptions.Count == 0)
            {
                throw new ValidationException($"attribute '{d.Key}': enum_options must list at least one option.");
            }

            if (compiled.EnumOptions.Count > Validation.Limits.MaxEnumOptions)
            {
                throw new ValidationException($"attribute '{d.Key}': enum_options must list at most {Validation.Limits.MaxEnumOptions} options.");
            }

            if (compiled.EnumOptions.Any(o => o.Length == 0 || o.Length > Validation.Limits.MaxEnumOptionLength || Validation.Guards.HasControlCharacters(o)))
            {
                throw new ValidationException($"attribute '{d.Key}': enum options must be 1-{Validation.Limits.MaxEnumOptionLength} printable characters.");
            }

            if (compiled.EnumOptions.Distinct(StringComparer.Ordinal).Count() != compiled.EnumOptions.Count)
            {
                throw new ValidationException($"attribute '{d.Key}': enum_options must not repeat.");
            }
        }
        else if (compiled.EnumOptions.Count > 0)
        {
            throw new ValidationException($"attribute '{d.Key}': enum_options is only valid for enum and multi_enum.");
        }

        if (compiled.Rules.Min.HasValue && compiled.Rules.Max.HasValue && compiled.Rules.Min > compiled.Rules.Max)
        {
            throw new ValidationException($"attribute '{d.Key}': validation.min must not exceed validation.max.");
        }

        if (compiled.Rules.Regex is not null)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(compiled.Rules.Regex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                throw new ValidationException($"attribute '{d.Key}': validation.regex is not a valid pattern.");
            }
        }

        // Money never lives in attributes; a registry key that looks like money is refused outright.
        if (d.Key is "price" or "amount" or "cost" or "currency" || d.Key.EndsWith("_price", StringComparison.Ordinal) || d.Key.StartsWith("price_", StringComparison.Ordinal))
        {
            throw new ValidationException($"attribute '{d.Key}': prices live on offers, not in attributes.");
        }
    }
}
