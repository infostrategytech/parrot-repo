using System.Globalization;
using System.Text;
using System.Text.Json;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Application.Filters;

public sealed record CompiledFilters(
    IReadOnlyList<string> Predicates,
    IReadOnlyList<SqlParameterSpec> Parameters,
    IReadOnlyList<string> Explanations)
{
    public static readonly CompiledFilters Empty = new([], [], []);
}

/// <summary>
/// Compiles the filter DSL to parameterised predicates over the derived index.
/// Keys must exist in the registry as filterable; the operator set is fixed; the typed
/// column comes from the registry's data_type; values become parameters. No key and no
/// value is ever written into the SQL string (spec: "never interpolate").
/// </summary>
public static class FilterCompiler
{
    private static readonly IReadOnlyDictionary<IndexColumn, string> Columns = new Dictionary<IndexColumn, string>
    {
        [IndexColumn.Numeric] = "numeric_value",
        [IndexColumn.Text] = "text_value",
        [IndexColumn.Bool] = "bool_value",
        [IndexColumn.Date] = "date_value",
    };

    /// <param name="filters">The request's filter map.</param>
    /// <param name="registry">Filterable attributes for the tenant (optionally scoped to one type).</param>
    /// <param name="indexRelation">A constant chosen by the caller, e.g. <c>catalog.v_product_attribute_index</c>. Never user input.</param>
    /// <param name="productAlias">Alias of the products relation in the outer query.</param>
    /// <param name="parameterPrefix">Prefix so several compiled fragments can share one command.</param>
    public static CompiledFilters Compile(
        IReadOnlyDictionary<string, FilterClause>? filters,
        IReadOnlyDictionary<string, FilterableAttribute> registry,
        string indexRelation,
        string productAlias,
        string parameterPrefix = "f")
    {
        if (filters is null || filters.Count == 0)
        {
            return CompiledFilters.Empty;
        }

        if (filters.Count > Limits.MaxFilterKeys)
        {
            throw new ValidationException($"filters may contain at most {Limits.MaxFilterKeys} keys.");
        }

        List<string> predicates = new List<string>();
        List<SqlParameterSpec> parameters = new List<SqlParameterSpec>();
        List<string> explanations = new List<string>();
        int n = 0;

        foreach ((string key, FilterClause clause) in filters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!Guards.AttributeKey().IsMatch(key))
            {
                throw new ValidationException($"filters.{key}: key must match ^[a-z][a-z0-9_]{{0,63}}$.");
            }

            if (!registry.TryGetValue(key, out FilterableAttribute? attr))
            {
                throw new ValidationException($"filters.{key}: not a filterable attribute.");
            }

            if (attr.Ambiguous)
            {
                throw new ValidationException($"filters.{key}: this key has different data types across product types; set product_type to disambiguate.");
            }

            if (clause is null || clause.IsEmpty)
            {
                throw new ValidationException($"filters.{key}: at least one of eq, in, gte, lte, between is required.");
            }

            string column = Columns[attr.Column];
            string keyParam = $"{parameterPrefix}k{n}";
            parameters.Add(new SqlParameterSpec(keyParam, key, SqlParamKind.Text));
            List<string> valuePredicates = new List<string>();

            if (clause.Eq is { } eq)
            {
                string p = $"{parameterPrefix}v{n}_eq";
                parameters.Add(new SqlParameterSpec(p, Convert(attr, eq, key, "eq"), KindFor(attr.Column)));
                valuePredicates.Add($"i.{column} = @{p}");
                explanations.Add($"{key} = {Describe(eq)}");
            }

            if (clause.In is { } inArr)
            {
                if (inArr.ValueKind != JsonValueKind.Array)
                {
                    throw new ValidationException($"filters.{key}.in must be an array.");
                }

                List<JsonElement> items = inArr.EnumerateArray().ToList();
                if (items.Count == 0 || items.Count > Limits.MaxInValues)
                {
                    throw new ValidationException($"filters.{key}.in must contain 1 to {Limits.MaxInValues} values.");
                }

                if (attr.Column == IndexColumn.Bool || attr.Column == IndexColumn.Date)
                {
                    throw new ValidationException($"filters.{key}.in is not supported for {EnumNames.ToSnake(attr.DataType)} attributes.");
                }

                string p = $"{parameterPrefix}v{n}_in";
                if (attr.Column == IndexColumn.Numeric)
                {
                    decimal[] values = items.Select(i => (decimal)Convert(attr, i, key, "in")).ToArray();
                    parameters.Add(new SqlParameterSpec(p, values, SqlParamKind.NumericArray));
                }
                else
                {
                    string[] values = items.Select(i => (string)Convert(attr, i, key, "in")).ToArray();
                    parameters.Add(new SqlParameterSpec(p, values, SqlParamKind.TextArray));
                }

                valuePredicates.Add($"i.{column} = ANY(@{p})");
                explanations.Add($"{key} in [{string.Join(", ", items.Select(Describe))}]");
            }

            if (clause.Gte is { } gte)
            {
                RequireOrdered(attr, key, "gte");
                string p = $"{parameterPrefix}v{n}_gte";
                parameters.Add(new SqlParameterSpec(p, Convert(attr, gte, key, "gte"), KindFor(attr.Column)));
                valuePredicates.Add($"i.{column} >= @{p}");
                explanations.Add($"{key} >= {Describe(gte)}");
            }

            if (clause.Lte is { } lte)
            {
                RequireOrdered(attr, key, "lte");
                string p = $"{parameterPrefix}v{n}_lte";
                parameters.Add(new SqlParameterSpec(p, Convert(attr, lte, key, "lte"), KindFor(attr.Column)));
                valuePredicates.Add($"i.{column} <= @{p}");
                explanations.Add($"{key} <= {Describe(lte)}");
            }

            if (clause.Between is { } between)
            {
                RequireOrdered(attr, key, "between");
                if (between.ValueKind != JsonValueKind.Array || between.GetArrayLength() != 2)
                {
                    throw new ValidationException($"filters.{key}.between must be an array of exactly two values.");
                }

                JsonElement lo = between[0];
                JsonElement hi = between[1];
                string pl = $"{parameterPrefix}v{n}_blo";
                string ph = $"{parameterPrefix}v{n}_bhi";
                parameters.Add(new SqlParameterSpec(pl, Convert(attr, lo, key, "between"), KindFor(attr.Column)));
                parameters.Add(new SqlParameterSpec(ph, Convert(attr, hi, key, "between"), KindFor(attr.Column)));
                valuePredicates.Add($"i.{column} >= @{pl} AND i.{column} <= @{ph}");
                explanations.Add($"{key} between {Describe(lo)} and {Describe(hi)}");
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("EXISTS (SELECT 1 FROM ").Append(indexRelation).Append(" i WHERE i.product_id = ")
              .Append(productAlias).Append(".id AND i.attribute_key = @").Append(keyParam)
              .Append(" AND ").Append(string.Join(" AND ", valuePredicates)).Append(')');
            predicates.Add(sb.ToString());
            n++;
        }

        return new CompiledFilters(predicates, parameters, explanations);
    }

    private static void RequireOrdered(FilterableAttribute attr, string key, string op)
    {
        if (attr.Column is IndexColumn.Text or IndexColumn.Bool)
        {
            throw new ValidationException($"filters.{key}.{op} is only valid for integer, number and date attributes.");
        }
    }

    private static SqlParamKind KindFor(IndexColumn column) => column switch
    {
        IndexColumn.Numeric => SqlParamKind.Numeric,
        IndexColumn.Text => SqlParamKind.Text,
        IndexColumn.Bool => SqlParamKind.Boolean,
        IndexColumn.Date => SqlParamKind.TimestampUtc,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static object Convert(FilterableAttribute attr, JsonElement value, string key, string op)
    {
        switch (attr.Column)
        {
            case IndexColumn.Numeric:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal d))
                {
                    return d;
                }

                throw new ValidationException($"filters.{key}.{op}: expected a number.");
            case IndexColumn.Text:
                if (value.ValueKind == JsonValueKind.String)
                {
                    string s = value.GetString()!;
                    if (s.Length == 0 || s.Length > 1024 || Guards.HasControlCharacters(s))
                    {
                        throw new ValidationException($"filters.{key}.{op}: invalid string value.");
                    }

                    return s;
                }

                throw new ValidationException($"filters.{key}.{op}: expected a string.");
            case IndexColumn.Bool:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }

                throw new ValidationException($"filters.{key}.{op}: expected true or false.");
            case IndexColumn.Date:
                if (value.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    return date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                }

                throw new ValidationException($"filters.{key}.{op}: expected a date YYYY-MM-DD.");
            default:
                throw new ValidationException($"filters.{key}: attribute is not filterable.");
        }
    }

    private static string Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        _ => e.GetRawText(),
    };
}
