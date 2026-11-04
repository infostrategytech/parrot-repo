using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

/// <summary>
/// Development-only catalogue client. Returns a fixed set of fake products instead of calling Meta.
/// Enabled via WhatsApp:MockCatalogue = true in appsettings.Development.json.
/// </summary>
public class MockMetaCatalogueClient : IMetaCatalogueClient
{
    private const string MockCatalogueId = "mock-catalogue-001";

    private static readonly IReadOnlyList<MetaCatalogueProduct> Products =
    [
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-001",
            RetailerId = "SKU-WBH-001",
            Name = "Wireless Bluetooth Headphones",
            Description = "Over-ear noise-cancelling headphones with 30-hour battery life and premium sound quality.",
            RawPrice = "45000 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Headphones",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "SoundWave",
            Condition = "new",
            Inventory = 12,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-002",
            RetailerId = "SKU-LW-002",
            Name = "Genuine Leather Wallet",
            Description = "Slim bifold wallet with RFID blocking, 8 card slots and a zippered coin pocket.",
            RawPrice = "8500 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Wallet",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "LeatherCo",
            Condition = "new",
            Inventory = 34,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-003",
            RetailerId = "SKU-SWB-003",
            Name = "Stainless Steel Water Bottle 1L",
            Description = "Double-walled insulated bottle. Keeps drinks cold 24 hours or hot 12 hours.",
            RawPrice = "3200 NGN",
            RawSalePrice = "2700 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Bottle",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "HydroMax",
            Condition = "new",
            Inventory = 57,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-004",
            RetailerId = "SKU-RS-004",
            Name = "Men's Running Shoes",
            Description = "Lightweight mesh upper with responsive foam sole. Available in sizes 40–46.",
            RawPrice = "28000 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Shoes",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "StridePro",
            Condition = "new",
            Inventory = 8,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-005",
            RetailerId = "SKU-PC-005",
            Name = "Phone Case (Universal Fit)",
            Description = "Shockproof TPU case compatible with most 6.1–6.7 inch smartphones.",
            RawPrice = "1800 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Case",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "ShieldUp",
            Condition = "new",
            Inventory = 120,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-006",
            RetailerId = "SKU-DL-006",
            Name = "LED Desk Lamp with USB Charging",
            Description = "5 brightness levels, touch control, 360° adjustable arm. Built-in USB-A charging port.",
            RawPrice = "12000 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Lamp",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "BrightSpace",
            Condition = "new",
            Inventory = 22,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-007",
            RetailerId = "SKU-NB-007",
            Name = "A5 Hardcover Notebook Set (3-pack)",
            Description = "Dot-grid, ruled and blank pages. 120gsm paper, lay-flat binding. Set of 3.",
            RawPrice = "2400 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=Notebooks",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "WriteRight",
            Condition = "new",
            Inventory = 45,
        },
        new MetaCatalogueProduct
        {
            MetaProductId = "mock-prod-008",
            RetailerId = "SKU-PB-008",
            Name = "Portable Charger 20000mAh",
            Description = "Dual USB-A + USB-C output. Fast charge 22.5W. LCD battery indicator.",
            RawPrice = "22000 NGN",
            RawSalePrice = "18500 NGN",
            Currency = "NGN",
            ImageUrl = "https://placehold.co/400x400?text=PowerBank",
            Availability = "in stock",
            ReviewStatus = "approved",
            Brand = "ChargeFast",
            Condition = "new",
            Inventory = 19,
        },
    ];

    public Task<string?> GetCatalogueIdAsync(string wabaId, string accessToken, CancellationToken ct) =>
        Task.FromResult<string?>(MockCatalogueId);

    public Task<MetaCatalogueProductsPage> GetApprovedProductsAsync(
        string catalogueId, string accessToken, string? cursor, CancellationToken ct) =>
        Task.FromResult(new MetaCatalogueProductsPage(Products, NextCursor: null));
}
