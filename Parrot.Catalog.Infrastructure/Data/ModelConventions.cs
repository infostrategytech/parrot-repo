using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Infrastructure.Data;

/// <summary>
/// Shared shape of the catalog model: snake_case names, snake_case enum strings with
/// CHECK constraints, jsonb for JSON text, timestamptz for every DateTime. Both the
/// writer (tables) and the reader (views) contexts run it so a column can never be
/// spelled two ways.
/// </summary>
internal static class ModelConventions
{
    public const string Schema = "catalog";

    public static readonly IReadOnlyDictionary<Type, string> Relations = new Dictionary<Type, string>
    {
        [typeof(Tenant)] = "tenants",
        [typeof(CatalogEntity)] = "catalogs",
        [typeof(Category)] = "categories",
        [typeof(CategoryClosure)] = "category_closure",
        [typeof(ProductType)] = "product_types",
        [typeof(AttributeDefinition)] = "attribute_definitions",
        [typeof(Product)] = "products",
        [typeof(ProductVariant)] = "product_variants",
        [typeof(ProductAttributeIndex)] = "product_attribute_index",
        [typeof(Offer)] = "offers",
        [typeof(Price)] = "prices",
        [typeof(Availability)] = "availability",
        [typeof(ProductRelationship)] = "product_relationships",
        [typeof(MediaAsset)] = "media",
        [typeof(ProductVersion)] = "product_versions",
        [typeof(CatalogDocument)] = "catalog_documents",
        [typeof(AuditLogEntry)] = "audit_log",
        [typeof(OutboxMessage)] = "outbox",
        [typeof(ConsumerInboxEntry)] = "consumer_inbox",
        [typeof(ItemAiStatus)] = "item_ai_status",
        [typeof(StockHold)] = "stock_holds",
    };

    private static readonly HashSet<string> JsonColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Product.Attributes), nameof(AttributeDefinition.EnumOptions), nameof(AttributeDefinition.Validation),
        nameof(Offer.Eligibility), nameof(ProductRelationship.Metadata), nameof(ProductVersion.Snapshot),
        nameof(OutboxMessage.Payload), nameof(AuditLogEntry.Before), nameof(AuditLogEntry.After), nameof(ItemAiStatus.Photos),
    };

    /// <summary>Applies names, converters, jsonb and check constraints. <paramref name="asViews"/> maps to v_* views instead of tables.</summary>
    public static void Apply(ModelBuilder builder, bool asViews)
    {
        builder.HasDefaultSchema(Schema);

        foreach (IMutableEntityType entityType in builder.Model.GetEntityTypes().ToList())
        {
            string relation = Relations[entityType.ClrType];
            EntityTypeBuilder entity = builder.Entity(entityType.ClrType);
            List<(string Name, string Sql)> checks = new List<(string, string)>();

            foreach (IMutableProperty property in entityType.GetProperties())
            {
                string column = EnumNames.ToSnake(property.Name);
                property.SetColumnName(column);

                Type underlying = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (underlying.IsEnum)
                {
                    property.SetValueConverter(ConverterFor(underlying));
                    property.SetMaxLength(32);
                    IEnumerable<string> values = Enum.GetValues(underlying).Cast<object>().Select(v => EnumNames.ToSnake(v.ToString()!));
                    string list = string.Join(", ", values.Select(v => $"'{v}'"));
                    checks.Add(($"ck_{relation}_{column}", $"{column} IN ({list})"));
                }
                else if (property.ClrType == typeof(string) && JsonColumns.Contains(property.Name))
                {
                    property.SetColumnType("jsonb");
                }
                else if (underlying == typeof(DateTime))
                {
                    property.SetColumnType("timestamp with time zone");
                }
                else if (underlying == typeof(decimal))
                {
                    property.SetColumnType("numeric(18,4)");
                }
            }

            if (asViews)
            {
                entity.ToView("v_" + relation, Schema);
            }
            else
            {
                entity.ToTable(relation, Schema, t =>
                {
                    foreach ((string name, string sql) in checks)
                    {
                        t.HasCheckConstraint(name, sql);
                    }
                });

                foreach (IMutableKey key in entityType.GetKeys())
                {
                    key.SetName(key.IsPrimaryKey() ? $"pk_{relation}" : $"ak_{relation}_{Columns(key.Properties)}");
                }

                foreach (IMutableForeignKey fk in entityType.GetForeignKeys())
                {
                    fk.SetConstraintName($"fk_{relation}_{Columns(fk.Properties)}");
                }

                foreach (IMutableIndex index in entityType.GetIndexes())
                {
                    index.SetDatabaseName($"ix_{relation}_{Columns(index.Properties)}");
                }
            }
        }
    }

    private static string Columns(IReadOnlyList<IMutableProperty> properties)
    {
        StringBuilder sb = new StringBuilder();
        foreach (IMutableProperty p in properties)
        {
            if (sb.Length > 0)
            {
                sb.Append('_');
            }

            sb.Append(EnumNames.ToSnake(p.Name));
        }

        return sb.ToString();
    }

    private static ValueConverter ConverterFor(Type enumType)
    {
        Type converterType = typeof(SnakeCaseEnumConverter<>).MakeGenericType(enumType);
        return (ValueConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class SnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public SnakeCaseEnumConverter()
        : base(v => EnumNames.ToSnake(v), s => Parse(s))
    {
    }

    private static TEnum Parse(string s)
        => EnumNames.TryParse(s, out TEnum value) ? value : throw new InvalidOperationException($"'{s}' is not a valid {typeof(TEnum).Name}.");
}
