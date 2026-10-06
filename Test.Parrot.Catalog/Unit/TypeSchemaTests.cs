using System.Text.Json;
using System.Text.Json.Nodes;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Catalog.Unit;

public class TypeSchemaTests
{
    private static readonly ProductType Laptop = new ProductType { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Key = "laptop", Name = "Laptop", Kind = ProductTypeKind.Good, Version = 1 };

    private static TypeSchema Schema() => new TypeSchema(Laptop,
    [
        Def("brand", AttributeDataType.String, required: true, filterable: true, validation: """{"max_length": 10}"""),
        Def("screen_inches", AttributeDataType.Number, filterable: true, unit: "in", validation: """{"min": 10, "max": 20}"""),
        Def("os", AttributeDataType.Enum, filterable: true, enumOptions: """["windows","linux"]"""),
        Def("ports", AttributeDataType.MultiEnum, filterable: true, enumOptions: """["usb_c","hdmi"]"""),
        Def("released", AttributeDataType.Date, filterable: true),
        Def("rugged", AttributeDataType.Boolean, filterable: true),
        Def("notes", AttributeDataType.Text),
        Def("extra", AttributeDataType.Json),
        Def("ram_gb", AttributeDataType.Integer, required: true, filterable: true, variantAxis: true, unit: "GB", validation: """{"min": 4}"""),
    ]);

    [Fact]
    public void Valid_product_attributes_pass()
    {
        IReadOnlyList<string> errors = Schema().Validate("""{"brand":"Parrot","screen_inches":14,"os":"linux","ports":["usb_c"],"released":"2026-01-15","rugged":true,"notes":"line\nline","extra":{"a":1}}""", AttributeScope.Product);
        Assert.Empty(errors);
    }

    [Fact]
    public void Unknown_key_is_rejected()
    {
        IReadOnlyList<string> errors = Schema().Validate("""{"brand":"Parrot","price":100}""", AttributeScope.Product);
        Assert.Contains(errors, e => e.Contains("attributes.price is not defined"));
    }

    [Fact]
    public void Missing_required_is_rejected()
    {
        IReadOnlyList<string> errors = Schema().Validate("""{"os":"linux"}""", AttributeScope.Product);
        Assert.Contains(errors, e => e.Contains("attributes.brand is required"));
    }

    [Fact]
    public void Wrong_types_and_bounds_are_rejected()
    {
        IReadOnlyList<string> errors = Schema().Validate("""{"brand":"Parrot","screen_inches":"14","os":"macos","ports":["usb_c","usb_c"],"released":"15/01/2026","rugged":"yes"}""", AttributeScope.Product);
        Assert.Contains(errors, e => e.Contains("screen_inches must be a number"));
        Assert.Contains(errors, e => e.Contains("os must be one of"));
        Assert.Contains(errors, e => e.Contains("ports must not contain duplicates"));
        Assert.Contains(errors, e => e.Contains("released must be a date"));
        Assert.Contains(errors, e => e.Contains("rugged must be true or false"));

        IReadOnlyList<string> bounds = Schema().Validate("""{"brand":"ParrotParrotParrot","screen_inches":42}""", AttributeScope.Product);
        Assert.Contains(bounds, e => e.Contains("brand must be at most 10"));
        Assert.Contains(bounds, e => e.Contains("screen_inches must be at most 20"));
    }

    [Fact]
    public void Variant_axis_keys_belong_on_variants_only()
    {
        Assert.Contains(Schema().Validate("""{"brand":"Parrot","ram_gb":16}""", AttributeScope.Product), e => e.Contains("variant-axis"));
        Assert.Empty(Schema().Validate("""{"ram_gb":16}""", AttributeScope.Variant));
        Assert.Contains(Schema().Validate("""{"brand":"x"}""", AttributeScope.Variant), e => e.Contains("cannot be set on a variant"));
        Assert.Contains(Schema().Validate("""{"ram_gb":2}""", AttributeScope.Variant), e => e.Contains("ram_gb must be at least 4"));
    }

    [Fact]
    public void Oversized_payload_and_non_object_are_rejected()
    {
        string big = "{\"brand\":\"" + new string('a', 70_000) + "\"}";
        Assert.Contains(Schema().Validate(big, AttributeScope.Product), e => e.Contains("at most 65536 bytes"));
        Assert.Contains(Schema().Validate("[1,2]", AttributeScope.Product), e => e.Contains("must be a JSON object"));
        Assert.Contains(Schema().Validate("{not json", AttributeScope.Product), e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void Json_schema_carries_types_constraints_and_ui_hints()
    {
        JsonObject schema = Schema().ToJsonSchema(AttributeScope.Product);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        JsonObject props = schema["properties"]!.AsObject();
        Assert.Equal("number", props["screen_inches"]!["type"]!.GetValue<string>());
        Assert.Equal(10, props["screen_inches"]!["minimum"]!.GetValue<decimal>());
        Assert.Equal("in", props["screen_inches"]!["x-ui"]!["unit"]!.GetValue<string>());
        Assert.Equal("date", props["released"]!["format"]!.GetValue<string>());
        Assert.Equal(2, props["os"]!["enum"]!.AsArray().Count);
        Assert.False(props.ContainsKey("ram_gb"));
        Assert.Contains("brand", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        JsonObject variant = Schema().ToJsonSchema(AttributeScope.Variant);
        Assert.True(variant["properties"]!.AsObject().ContainsKey("ram_gb"));
        Assert.Equal("GB", variant["properties"]!["ram_gb"]!["x-ui"]!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Definition_rules_refuse_money_keys_and_bad_definitions()
    {
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("price", AttributeDataType.Number)));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("unit_price", AttributeDataType.Number)));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("Notes", AttributeDataType.Text)));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("notes", AttributeDataType.Text, filterable: true)));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("os", AttributeDataType.Enum)));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("os", AttributeDataType.Enum, enumOptions: """["a","a"]""")));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("w", AttributeDataType.Number, validation: """{"min": 5, "max": 1}""")));
        Assert.Throws<ValidationException>(() => AttributeDefinitionRules.Check(Def("w", AttributeDataType.String, validation: """{"regex": "(["}""")));
        AttributeDefinitionRules.Check(Def("weight_kg", AttributeDataType.Number, filterable: true, unit: "kg"));
    }

    [Fact]
    public void Derived_index_rows_follow_data_types_and_fan_out_multi_enum()
    {
        TypeSchema schema = Schema();
        Guid tenant = Guid.NewGuid();
        Guid product = Guid.NewGuid();
        IReadOnlyList<ProductAttributeIndex> rows = DerivedIndexBuilder.BuildRows(schema, tenant, Guid.NewGuid(), product, null,
            """{"brand":"Parrot","screen_inches":14.5,"os":"linux","ports":["usb_c","hdmi"],"released":"2026-01-15","rugged":false,"notes":"not indexed","extra":{"x":1}}""");

        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Equal(tenant, r.TenantId));
        Assert.Equal("Parrot", rows.Single(r => r.AttributeKey == "brand").TextValue);
        Assert.Equal(14.5m, rows.Single(r => r.AttributeKey == "screen_inches").NumericValue);
        Assert.Equal(2, rows.Count(r => r.AttributeKey == "ports"));
        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), rows.Single(r => r.AttributeKey == "released").DateValue);
        Assert.False(rows.Single(r => r.AttributeKey == "rugged").BoolValue);
        Assert.DoesNotContain(rows, r => r.AttributeKey is "notes" or "extra");

        string search = DerivedIndexBuilder.BuildSearchText(schema, "ProBook", "LPT-1", "desc", """{"brand":"Parrot"}""", []);
        Assert.Contains("ProBook", search);
        Assert.Contains("LPT-1", search);
        Assert.DoesNotContain("Parrot", search); // brand is not flagged searchable in this schema
    }

    private static AttributeDefinition Def(string key, AttributeDataType type, bool required = false, bool filterable = false, bool variantAxis = false, string? unit = null, string? enumOptions = null, string? validation = null)
        => new AttributeDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = Laptop.TenantId,
            ProductTypeId = Laptop.Id,
            TypeVersion = 1,
            Key = key,
            Label = key.Replace('_', ' '),
            DataType = type,
            Required = required,
            Filterable = filterable,
            VariantAxis = variantAxis,
            Unit = unit,
            EnumOptions = enumOptions,
            Validation = validation,
        };
}
