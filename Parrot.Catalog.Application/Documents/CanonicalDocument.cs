using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Documents;

/// <summary>Everything the renderer needs, already loaded by the caller inside its transaction.</summary>
public sealed record RenderInput(
    Product Product,
    TypeSchema Schema,
    IReadOnlyList<string> CategoryPath,
    IReadOnlyList<ProductVariant> Variants,
    IReadOnlyList<(Offer Offer, IReadOnlyList<Price> Prices)> Offers,
    IReadOnlyList<Availability> Availability,
    IReadOnlyList<(RelationshipType Type, string TargetName, string TargetSku)> Related,
    DateTime AsOf);

public sealed record RenderedDocument(string Text, string ContentHash);

/// <summary>
/// Renders a published product into deterministic text using registry labels and
/// units. Phase 1 stores the text and hash; Phase 2 embeds it. The interface is the
/// seam: a different renderer (per locale, per channel) is a new implementation.
/// </summary>
public interface ICanonicalDocumentRenderer
{
    RenderedDocument Render(RenderInput input);
}

public sealed class CanonicalDocumentRenderer : ICanonicalDocumentRenderer
{
    public RenderedDocument Render(RenderInput input)
    {
        StringBuilder sb = new StringBuilder();
        Product p = input.Product;
        sb.Append("Product: ").Append(p.Name).Append(" (SKU ").Append(p.Sku).Append(")\n");
        sb.Append("Type: ").Append(input.Schema.TypeName).Append(" [").Append(EnumNames.ToSnake(input.Schema.Kind)).Append("]\n");
        if (input.CategoryPath.Count > 0)
        {
            sb.Append("Category: ").Append(string.Join(" > ", input.CategoryPath)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            sb.Append("Description: ").Append(p.Description.Trim()).Append('\n');
        }

        AppendSpecifications(sb, "Specifications", input.Schema, p.Attributes, AttributeScope.Product);

        foreach (ProductVariant v in input.Variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku, StringComparer.Ordinal))
        {
            sb.Append("Variant: ").Append(v.Name).Append(" (SKU ").Append(v.Sku).Append(")\n");
            AppendSpecifications(sb, "  Variant specifications", input.Schema, v.Attributes, AttributeScope.Variant);
        }

        Dictionary<Guid, ProductVariant> variantsById = input.Variants.ToDictionary(v => v.Id);
        if (input.Offers.Count > 0)
        {
            sb.Append("Offers and current prices:\n");
            foreach ((Offer offer, IReadOnlyList<Price> prices) in input.Offers
                .OrderBy(o => o.Offer.VariantId is null ? 0 : 1)
                .ThenBy(o => o.Offer.VariantId is { } vid && variantsById.TryGetValue(vid, out ProductVariant? v) ? v.SortOrder : 0)
                .ThenBy(o => o.Offer.SortOrder).ThenBy(o => o.Offer.Name, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(offer.Name);
                if (offer.VariantId is { } offerVariant && variantsById.TryGetValue(offerVariant, out ProductVariant? forVariant))
                {
                    sb.Append(" for ").Append(forVariant.Name).Append(" (SKU ").Append(forVariant.Sku).Append(')');
                }

                sb.Append(" (").Append(EnumNames.ToSnake(offer.BillingPeriod)).Append(')');
                if (offer.PriceOnRequest)
                {
                    sb.Append(": price on request");
                }
                else if (prices.Count > 0)
                {
                    sb.Append(": ");
                    sb.Append(string.Join(", ", prices
                        .OrderBy(pr => pr.Currency, StringComparer.Ordinal).ThenBy(pr => pr.PriceType)
                        .Select(pr => $"{pr.Amount.ToString("0.####", CultureInfo.InvariantCulture)} {pr.Currency} ({EnumNames.ToSnake(pr.PriceType)})")));
                    if (offer.Unit is { } unit)
                    {
                        sb.Append(" per ").Append(unit);
                    }
                }

                sb.Append('\n');
            }
        }

        if (input.Availability.Count > 0)
        {
            sb.Append("Availability:\n");
            foreach (Availability a in input.Availability
                .OrderBy(a => a.Region, StringComparer.Ordinal)
                .ThenBy(a => a.VariantId is { } vid && variantsById.TryGetValue(vid, out ProductVariant? v) ? v.SortOrder : -1))
            {
                sb.Append("  - ").Append(a.Region == "*" ? "all regions" : a.Region);
                if (a.VariantId is { } availabilityVariant && variantsById.TryGetValue(availabilityVariant, out ProductVariant? forVariant))
                {
                    sb.Append(" [").Append(forVariant.Sku).Append(']');
                }

                sb.Append(": ").Append(EnumNames.ToSnake(a.Mode).Replace('_', ' '));
                if (a.LeadTimeDays.HasValue)
                {
                    sb.Append(", ready in ").Append(a.LeadTimeDays.Value.ToString(CultureInfo.InvariantCulture)).Append(" days");
                }

                if (a.StockQty.HasValue)
                {
                    sb.Append(", stock ").Append(a.StockQty.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (a.Capacity.HasValue)
                {
                    sb.Append(", capacity ").Append(a.Capacity.Value.ToString(CultureInfo.InvariantCulture));
                }

                sb.Append('\n');
            }
        }

        if (input.Related.Count > 0)
        {
            sb.Append("Related products:\n");
            foreach ((RelationshipType type, string name, string sku) in input.Related.OrderBy(r => r.Type).ThenBy(r => r.TargetSku, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(EnumNames.ToSnake(type).Replace('_', ' ')).Append(": ").Append(name).Append(" (SKU ").Append(sku).Append(")\n");
            }
        }

        string text = sb.ToString();
        return new RenderedDocument(text, Hash(text));
    }

    public static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void AppendSpecifications(StringBuilder sb, string heading, TypeSchema schema, string attributesJson, AttributeScope scope)
    {
        using JsonDocument doc = JsonDocument.Parse(attributesJson);
        List<string> lines = new List<string>();
        foreach (CompiledAttribute def in schema.For(scope))
        {
            if (!doc.RootElement.TryGetProperty(def.Key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            string rendered = RenderValue(def, value);
            lines.Add($"  - {def.Label}: {rendered}");
        }

        if (lines.Count > 0)
        {
            sb.Append(heading).Append(":\n");
            foreach (string line in lines)
            {
                sb.Append(line).Append('\n');
            }
        }
    }

    public static string RenderValue(CompiledAttribute def, JsonElement value)
    {
        string core = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
            _ => value.GetRawText(),
        };
        return string.IsNullOrEmpty(def.Unit) ? core : $"{core} {def.Unit}";
    }
}
