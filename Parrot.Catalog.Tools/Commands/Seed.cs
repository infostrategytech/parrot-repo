using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Tools.Commands;

/// <summary>
/// The reference catalog from the spec: a laptop with variants, a consulting service,
/// an internet plan with Starter/Pro/Enterprise offers in NGN and USD, and a bundle
/// expressed through relationships. Everything goes through the admin services, so
/// validation, the derived index, versions, audit and outbox all fire as in production.
/// </summary>
public static class Seed
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        Guid tenantId = Args.RequireGuid(args, "tenant");
        string catalogKey = args.GetValueOrDefault("catalog") ?? "main";
        await using ServiceProvider provider = Hosting.Services(configuration);
        using IServiceScope scope = Hosting.TenantScope(provider, tenantId, actor: "seed");
        SeedResult result = await SeedAsync(scope.ServiceProvider, catalogKey, CancellationToken.None);
        Console.WriteLine($"Seeded tenant {tenantId}: catalog {result.CatalogId}, {result.ProductIds.Count(k => !k.Key.Contains("variant", StringComparison.Ordinal))} published products.");
        foreach ((string key, Guid id) in result.ProductIds)
        {
            Console.WriteLine($"  {key}: {id}");
        }

        return 0;
    }

    public static async Task<SeedResult> SeedAsync(IServiceProvider services, string catalogKey, CancellationToken ct)
    {
        ICatalogAdminService catalogs = services.GetRequiredService<ICatalogAdminService>();
        IProductTypeAdminService types = services.GetRequiredService<IProductTypeAdminService>();
        ICategoryAdminService categories = services.GetRequiredService<ICategoryAdminService>();
        IProductAdminService products = services.GetRequiredService<IProductAdminService>();
        IOfferAdminService offers = services.GetRequiredService<IOfferAdminService>();
        IAvailabilityAdminService availability = services.GetRequiredService<IAvailabilityAdminService>();
        IRelationshipAdminService relationships = services.GetRequiredService<IRelationshipAdminService>();

        CatalogResponse catalog = await catalogs.CreateAsync(new CreateCatalogRequest(catalogKey, "Main catalog", "NGN"), ct);

        // ---- types ------------------------------------------------------------
        ProductTypeResponse laptop = await types.CreateAsync(new CreateProductTypeRequest("laptop", "Laptop", ProductTypeKind.Good,
        [
            new AttributeDefinitionInput("brand", "Brand", AttributeDataType.String, Filterable: true, Searchable: true, Required: true, DisplayGroup: "General", SortOrder: 1),
            new AttributeDefinitionInput("screen_inches", "Screen size", AttributeDataType.Number, Unit: "in", Filterable: true, DisplayGroup: "Display", SortOrder: 2, Validation: El("""{"min": 10, "max": 20}""")),
            new AttributeDefinitionInput("os", "Operating system", AttributeDataType.Enum, EnumOptions: ["windows", "linux", "none"], Filterable: true, DisplayGroup: "General", SortOrder: 3),
            new AttributeDefinitionInput("ports", "Ports", AttributeDataType.MultiEnum, EnumOptions: ["usb_c", "usb_a", "hdmi", "ethernet", "sd"], Filterable: true, DisplayGroup: "Connectivity", SortOrder: 4),
            new AttributeDefinitionInput("ram_gb", "Memory", AttributeDataType.Integer, Unit: "GB", Filterable: true, Required: true, VariantAxis: true, DisplayGroup: "Performance", SortOrder: 10, Validation: El("""{"min": 4, "max": 128}""")),
            new AttributeDefinitionInput("storage_gb", "Storage", AttributeDataType.Integer, Unit: "GB", Filterable: true, Required: true, VariantAxis: true, DisplayGroup: "Performance", SortOrder: 11),
            new AttributeDefinitionInput("colour", "Colour", AttributeDataType.Enum, EnumOptions: ["silver", "black", "blue"], Filterable: true, Required: true, VariantAxis: true, DisplayGroup: "Appearance", SortOrder: 12),
        ]), ct);

        ProductTypeResponse consulting = await types.CreateAsync(new CreateProductTypeRequest("consulting", "Consulting engagement", ProductTypeKind.Service,
        [
            new AttributeDefinitionInput("duration_days", "Duration", AttributeDataType.Integer, Unit: "days", Filterable: true, Required: true, SortOrder: 1, Validation: El("""{"min": 1, "max": 365}""")),
            new AttributeDefinitionInput("includes_training", "Includes training", AttributeDataType.Boolean, Filterable: true, Required: true, SortOrder: 2),
            new AttributeDefinitionInput("support_months", "Post-engagement support", AttributeDataType.Integer, Unit: "months", Filterable: true, SortOrder: 3),
            new AttributeDefinitionInput("delivery", "Delivery mode", AttributeDataType.Enum, EnumOptions: ["remote", "on_site", "hybrid"], Filterable: true, Searchable: true, SortOrder: 4),
        ]), ct);

        ProductTypeResponse internetPlan = await types.CreateAsync(new CreateProductTypeRequest("internet_plan", "Internet plan", ProductTypeKind.Subscription,
        [
            new AttributeDefinitionInput("speed_mbps", "Download speed", AttributeDataType.Integer, Unit: "Mbps", Filterable: true, Required: true, SortOrder: 1),
            new AttributeDefinitionInput("max_users", "Recommended users", AttributeDataType.Integer, Filterable: true, SortOrder: 2),
            new AttributeDefinitionInput("contract_months", "Minimum contract", AttributeDataType.Integer, Unit: "months", Filterable: true, Required: true, SortOrder: 3),
            new AttributeDefinitionInput("technology", "Technology", AttributeDataType.Enum, EnumOptions: ["fibre", "fixed_wireless", "satellite"], Filterable: true, Searchable: true, SortOrder: 4),
            new AttributeDefinitionInput("installation_from", "Installation available from", AttributeDataType.Date, Filterable: true, SortOrder: 5),
        ]), ct);

        ProductTypeResponse bundle = await types.CreateAsync(new CreateProductTypeRequest("bundle", "Bundle", ProductTypeKind.Bundle,
        [
            new AttributeDefinitionInput("audience", "Designed for", AttributeDataType.String, Searchable: true, SortOrder: 1),
            new AttributeDefinitionInput("savings_percent", "Savings versus buying separately", AttributeDataType.Number, Unit: "%", Filterable: true, SortOrder: 2, Validation: El("""{"min": 0, "max": 100}""")),
            new AttributeDefinitionInput("notes", "Notes", AttributeDataType.Text, SortOrder: 3),
        ]), ct);

        // ---- categories -------------------------------------------------------
        CategoryResponse hardware = await categories.CreateAsync(new CreateCategoryRequest(catalog.Id, null, "hardware", "Hardware", SortOrder: 1), ct);
        CategoryResponse laptops = await categories.CreateAsync(new CreateCategoryRequest(catalog.Id, hardware.Id, "laptops", "Laptops", SortOrder: 1), ct);
        CategoryResponse servicesCat = await categories.CreateAsync(new CreateCategoryRequest(catalog.Id, null, "services", "Services", SortOrder: 2), ct);
        CategoryResponse connectivity = await categories.CreateAsync(new CreateCategoryRequest(catalog.Id, null, "connectivity", "Connectivity", SortOrder: 3), ct);
        CategoryResponse bundles = await categories.CreateAsync(new CreateCategoryRequest(catalog.Id, null, "bundles", "Bundles", SortOrder: 4), ct);

        // ---- laptop with variants --------------------------------------------
        ProductAdminResponse probook = await products.CreateAsync(new CreateProductRequest(catalog.Id, laptop.Id, laptops.Id, "LPT-PB14", "ProBook 14",
            El("""{"brand": "Parrot", "screen_inches": 14, "os": "windows", "ports": ["usb_c", "usb_a", "hdmi"]}"""),
            Description: "A 14-inch business laptop with all-day battery life and a spill-resistant keyboard."), ct);
        VariantResponse pb8 = await products.CreateVariantAsync(probook.Id, new CreateVariantRequest("LPT-PB14-8-256-SL", "ProBook 14 8GB/256GB Silver", El("""{"ram_gb": 8, "storage_gb": 256, "colour": "silver"}"""), SortOrder: 1), ct);
        VariantResponse pb16 = await products.CreateVariantAsync(probook.Id, new CreateVariantRequest("LPT-PB14-16-512-BK", "ProBook 14 16GB/512GB Black", El("""{"ram_gb": 16, "storage_gb": 512, "colour": "black"}"""), SortOrder: 2), ct);
        VariantResponse pb32 = await products.CreateVariantAsync(probook.Id, new CreateVariantRequest("LPT-PB14-32-1024-BK", "ProBook 14 32GB/1TB Black", El("""{"ram_gb": 32, "storage_gb": 1024, "colour": "black"}"""), SortOrder: 3), ct);
        foreach ((VariantResponse v, decimal ngn, decimal usd) in new[] { (pb8, 950_000m, 620m), (pb16, 1_250_000m, 810m), (pb32, 1_690_000m, 1_090m) })
        {
            OfferAdminResponse offer = await offers.CreateAsync(new CreateOfferRequest(null, v.Id, "One-off purchase", BillingPeriod.OneTime, Jan1, Status: OfferStatus.Active), ct);
            await offers.AddPriceAsync(offer.Id, new CreatePriceRequest(ngn, "NGN", PriceType.List, Jan1), ct);
            await offers.AddPriceAsync(offer.Id, new CreatePriceRequest(usd, "USD", PriceType.List, Jan1), ct);
            await availability.CreateAsync(new CreateAvailabilityRequest(null, v.Id, "NG", Jan1, StockQty: v == pb32 ? 0 : 25), ct);
        }

        // ---- consulting service -----------------------------------------------
        ProductAdminResponse onboarding = await products.CreateAsync(new CreateProductRequest(catalog.Id, consulting.Id, servicesCat.Id, "SVC-ONB-10", "CRM onboarding (10 days)",
            El("""{"duration_days": 10, "includes_training": true, "support_months": 3, "delivery": "hybrid"}"""),
            Description: "Ten consulting days to migrate your contacts, configure channels and train the team."), ct);
        OfferAdminResponse onboardingOffer = await offers.CreateAsync(new CreateOfferRequest(onboarding.Id, null, "Fixed price", BillingPeriod.OneTime, Jan1, Status: OfferStatus.Active), ct);
        await offers.AddPriceAsync(onboardingOffer.Id, new CreatePriceRequest(2_400_000m, "NGN", PriceType.List, Jan1), ct);
        await offers.AddPriceAsync(onboardingOffer.Id, new CreatePriceRequest(1_600m, "USD", PriceType.List, Jan1), ct);
        await availability.CreateAsync(new CreateAvailabilityRequest(onboarding.Id, null, "*", Jan1, Capacity: 4), ct);

        // ---- internet plan with Starter / Pro / Enterprise --------------------
        ProductAdminResponse fibre = await products.CreateAsync(new CreateProductRequest(catalog.Id, internetPlan.Id, connectivity.Id, "NET-FIBRE-HOME", "Home Fibre",
            El("""{"speed_mbps": 100, "max_users": 10, "contract_months": 12, "technology": "fibre", "installation_from": "2026-01-15"}"""),
            Description: "Uncapped fibre to the home with a free router and 24/7 support."), ct);
        foreach ((string name, decimal ngn, decimal usd, int sort) in new[] { ("Starter", 25_000m, 16m, 1), ("Pro", 45_000m, 29m, 2), ("Enterprise", 120_000m, 78m, 3) })
        {
            OfferAdminResponse offer = await offers.CreateAsync(new CreateOfferRequest(fibre.Id, null, name, BillingPeriod.Monthly, Jan1, Status: OfferStatus.Active, SortOrder: sort,
                Eligibility: El(name == "Enterprise" ? """{"requires": ["business_account"]}""" : "{}")), ct);
            await offers.AddPriceAsync(offer.Id, new CreatePriceRequest(ngn, "NGN", PriceType.List, Jan1), ct);
            await offers.AddPriceAsync(offer.Id, new CreatePriceRequest(usd, "USD", PriceType.List, Jan1), ct);
            if (name == "Starter")
            {
                // A promo that ended before "now": only visible with as_of in the past.
                await offers.AddPriceAsync(offer.Id, new CreatePriceRequest(19_999m, "NGN", PriceType.Promo, Jan1, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)), ct);
            }
        }

        await availability.CreateAsync(new CreateAvailabilityRequest(fibre.Id, null, "NG", Jan1, Capacity: 500), ct);

        // ---- bundle through relationships -------------------------------------
        ProductAdminResponse wfh = await products.CreateAsync(new CreateProductRequest(catalog.Id, bundle.Id, bundles.Id, "BDL-WFH", "Work-from-home bundle",
            El("""{"audience": "remote teams", "savings_percent": 12.5, "notes": "Laptop, fibre and onboarding together."}"""),
            Description: "A ProBook 14, Home Fibre and CRM onboarding in one order."), ct);
        OfferAdminResponse wfhOffer = await offers.CreateAsync(new CreateOfferRequest(wfh.Id, null, "Bundle price", BillingPeriod.OneTime, Jan1, Status: OfferStatus.Active), ct);
        await offers.AddPriceAsync(wfhOffer.Id, new CreatePriceRequest(3_900_000m, "NGN", PriceType.List, Jan1), ct);
        await relationships.CreateAsync(new CreateRelationshipRequest(wfh.Id, probook.Id, RelationshipType.BundledWith, El("""{"quantity": 1}""")), ct);
        await relationships.CreateAsync(new CreateRelationshipRequest(wfh.Id, fibre.Id, RelationshipType.BundledWith, El("""{"quantity": 1, "offer": "Pro"}""")), ct);
        await relationships.CreateAsync(new CreateRelationshipRequest(wfh.Id, onboarding.Id, RelationshipType.BundledWith), ct);
        await relationships.CreateAsync(new CreateRelationshipRequest(fibre.Id, probook.Id, RelationshipType.CrossSell), ct);
        await relationships.CreateAsync(new CreateRelationshipRequest(probook.Id, onboarding.Id, RelationshipType.Upsell), ct);

        // ---- publish ------------------------------------------------------------
        foreach (Guid id in new[] { probook.Id, onboarding.Id, fibre.Id, wfh.Id })
        {
            await products.PublishAsync(id, ct);
        }

        return new SeedResult(catalog.Id, new Dictionary<string, Guid>
        {
            ["laptop"] = probook.Id,
            ["laptop_variant_16gb"] = pb16.Id,
            ["consulting"] = onboarding.Id,
            ["internet_plan"] = fibre.Id,
            ["bundle"] = wfh.Id,
        }, new Dictionary<string, Guid>
        {
            ["laptop"] = laptop.Id,
            ["consulting"] = consulting.Id,
            ["internet_plan"] = internetPlan.Id,
            ["bundle"] = bundle.Id,
        }, new Dictionary<string, Guid>
        {
            ["hardware"] = hardware.Id,
            ["laptops"] = laptops.Id,
            ["services"] = servicesCat.Id,
            ["connectivity"] = connectivity.Id,
            ["bundles"] = bundles.Id,
        });
    }

    private static readonly DateTime Jan1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public sealed record SeedResult(Guid CatalogId, IReadOnlyDictionary<string, Guid> ProductIds, IReadOnlyDictionary<string, Guid> TypeIds, IReadOnlyDictionary<string, Guid> CategoryIds);
