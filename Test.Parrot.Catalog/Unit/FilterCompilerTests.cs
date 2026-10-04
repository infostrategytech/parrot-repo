using System.Text.Json;
using Parrot.Catalog.Application.Filters;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Catalog.Unit;

public class FilterCompilerTests
{
    private static readonly Dictionary<string, FilterableAttribute> Registry = new(StringComparer.Ordinal)
    {
        ["ram_gb"] = new FilterableAttribute("ram_gb", AttributeDataType.Integer, IndexColumn.Numeric, "Memory", "GB", false),
        ["colour"] = new FilterableAttribute("colour", AttributeDataType.Enum, IndexColumn.Text, "Colour", null, false),
        ["rugged"] = new FilterableAttribute("rugged", AttributeDataType.Boolean, IndexColumn.Bool, "Rugged", null, false),
        ["released"] = new FilterableAttribute("released", AttributeDataType.Date, IndexColumn.Date, "Released", null, false),
        ["speed"] = new FilterableAttribute("speed", AttributeDataType.Integer, IndexColumn.Numeric, "Speed", null, true),
    };

    private static Dictionary<string, FilterClause> Filters(string json)
        => JsonSerializer.Deserialize<Dictionary<string, FilterClause>>(json)!;

    [Fact]
    public void Compiles_to_exists_predicates_with_parameters_only()
    {
        CompiledFilters compiled = FilterCompiler.Compile(
            Filters("""{"ram_gb": {"gte": 16, "lte": 64}, "colour": {"in": ["black", "silver"]}, "rugged": {"eq": true}, "released": {"between": ["2025-01-01", "2026-12-31"]}}"""),
            Registry, "catalog.v_product_attribute_index", "p");

        Assert.Equal(4, compiled.Predicates.Count);
        string all = string.Join(" AND ", compiled.Predicates);
        Assert.Contains("EXISTS (SELECT 1 FROM catalog.v_product_attribute_index i WHERE i.product_id = p.id AND i.attribute_key = @fk0", all);
        Assert.Contains("i.numeric_value >= @fv", all);
        Assert.Contains("i.text_value = ANY(@fv", all);
        Assert.Contains("i.bool_value = @fv", all);
        Assert.Contains("i.date_value >= @fv", all);

        // Neither keys nor values appear in SQL text.
        Assert.DoesNotContain("ram_gb", all);
        Assert.DoesNotContain("black", all);
        Assert.DoesNotContain("16", all);
        Assert.DoesNotContain("2025", all);

        Assert.Contains(compiled.Parameters, p => p.Kind == SqlParamKind.Text && (string?)p.Value == "ram_gb");
        Assert.Contains(compiled.Parameters, p => p.Kind == SqlParamKind.Numeric && (decimal?)p.Value == 16m);
        Assert.Contains(compiled.Parameters, p => p.Kind == SqlParamKind.TextArray && ((string[])p.Value!).SequenceEqual(["black", "silver"]));
        Assert.Contains(compiled.Parameters, p => p.Kind == SqlParamKind.Boolean && (bool?)p.Value == true);
        Assert.Contains(compiled.Parameters, p => p.Kind == SqlParamKind.TimestampUtc);
        Assert.Contains(compiled.Explanations, e => e == "ram_gb >= 16");
        Assert.Contains(compiled.Explanations, e => e == "colour in [black, silver]");
    }

    [Theory]
    [InlineData("""{"price": {"eq": 1}}""", "not a filterable attribute")]
    [InlineData("""{"Ram_gb": {"eq": 1}}""", "key must match")]
    [InlineData("""{"ram_gb; DROP TABLE": {"eq": 1}}""", "key must match")]
    [InlineData("""{"ram_gb": {}}""", "at least one of")]
    [InlineData("""{"ram_gb": {"eq": "sixteen"}}""", "expected a number")]
    [InlineData("""{"colour": {"gte": "a"}}""", "only valid for integer, number and date")]
    [InlineData("""{"rugged": {"in": [true]}}""", "not supported for boolean")]
    [InlineData("""{"released": {"eq": "yesterday"}}""", "expected a date")]
    [InlineData("""{"ram_gb": {"between": [1]}}""", "exactly two values")]
    [InlineData("""{"ram_gb": {"in": []}}""", "1 to 50 values")]
    [InlineData("""{"speed": {"eq": 1}}""", "different data types across product types")]
    public void Rejects_invalid_filters_with_400(string json, string expected)
    {
        ValidationException ex = Assert.Throws<ValidationException>(() => FilterCompiler.Compile(Filters(json), Registry, "idx", "p"));
        Assert.Contains(expected, ex.Message + string.Join(" ", ex.Errors));
    }

    [Fact]
    public void Too_many_keys_is_rejected()
    {
        Dictionary<string, FilterableAttribute> many = new(StringComparer.Ordinal);
        Dictionary<string, FilterClause> filters = new(StringComparer.Ordinal);
        for (int i = 0; i < 21; i++)
        {
            many[$"k{i}"] = new FilterableAttribute($"k{i}", AttributeDataType.Integer, IndexColumn.Numeric, "k", null, false);
            filters[$"k{i}"] = JsonSerializer.Deserialize<FilterClause>("""{"eq": 1}""")!;
        }

        Assert.Throws<ValidationException>(() => FilterCompiler.Compile(filters, many, "idx", "p"));
    }

    [Fact]
    public void Empty_filters_compile_to_nothing()
    {
        Assert.Empty(FilterCompiler.Compile(null, Registry, "idx", "p").Predicates);
        Assert.Empty(FilterCompiler.Compile(new Dictionary<string, FilterClause>(), Registry, "idx", "p").Predicates);
    }
}
