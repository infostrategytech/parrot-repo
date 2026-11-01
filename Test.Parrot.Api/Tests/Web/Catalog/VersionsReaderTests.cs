using Parrot.Web.Features.Dashboard.Catalog;
using Parrot.Web.Features.Dashboard.Catalog.ConceptE;

namespace Test.Parrot.Api.Tests.Web.Catalog;

/// <summary>
/// A post that prices each version ("Uk 6 blue 50k", "iPhone 14 Pro 128gb blue 1.15m", "50kg 70,000") comes in
/// as a list of those versions only, each written as it reads and at its own price: never every combination.
/// </summary>
public class VersionsReaderTests
{
    private static CatalogEntry Item(string caption)
    {
        var entry = new CatalogEntry();
        DemoCaptionReader.Read(caption).ApplyOffer(entry);
        return entry;
    }

    private static IEnumerable<(string Key, decimal? Price)> Versions(CatalogEntry entry) =>
        entry.Variants.Select(variant => (variant.Key, variant.Price));

    [Fact]
    public void SizeAndColour_KeepsOnlyTheVersionsWritten()
    {
        var entry = Item("Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k\nUk 14 yellow 64k\nDm to order.");

        Assert.Equal("Ankara two-piece set", DemoCaptionReader.Read("Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k").Name);
        Assert.Equal([("UK 6 Blue", 50000m), ("UK 12 Red", 55000m), ("UK 14 Yellow", 64000m)], Versions(entry));
        Assert.Equal(PriceWords.VariantList, Assert.Single(entry.Options).Name);
        Assert.Null(entry.Price);
    }

    [Fact]
    public void StorageColourAndCondition_ReadAsOneVersion()
    {
        var reading = DemoCaptionReader.Read("iPhone 14 Pro\n128gb blue brand new - 1.35m\n128gb blue UK used - 1.1m\n256gb black UK used - 1.25m\n256gb gold brand new - 1.55m");
        var entry = new CatalogEntry();
        reading.ApplyOffer(entry);

        Assert.Equal("iPhone 14 Pro", reading.Name);
        Assert.Equal([("128GB Blue Brand new", 1_350_000m), ("128GB Blue UK used", 1_100_000m), ("256GB Black UK used", 1_250_000m), ("256GB Gold Brand new", 1_550_000m)], Versions(entry));
    }

    [Fact]
    public void DifferentModels_KeepTheirNames()
    {
        var entry = Item("Fairly used iPhones\niPhone 13 128gb 650k\niPhone 14 Pro 256gb 1.1m\niPhone 15 Pro Max 256gb 1.45m");

        Assert.Equal(["iPhone 13 128GB", "iPhone 14 Pro 256GB", "iPhone 15 Pro Max 256GB"], entry.Variants.Select(variant => variant.Key));
    }

    [Fact]
    public void NameWrittenOnceOnOneLine_IsTheName()
    {
        var reading = DemoCaptionReader.Read("iPhone 14 Pro 128gb blue 1.15m, 256gb black 1.3m");
        var entry = new CatalogEntry();
        reading.ApplyOffer(entry);

        Assert.Equal("iPhone 14 Pro", reading.Name);
        Assert.Equal([("128GB Blue", 1_150_000m), ("256GB Black", 1_300_000m)], Versions(entry));
    }

    [Fact]
    public void BagSizes_EachAVersion()
    {
        var reading = DemoCaptionReader.Read("MAs choice rice\n50kg 70,000\n25kg 35,000\n12.5kg 18,500\n10kg 14,500\n5kg 7,500\n\nSend a DM or WhatsApp 08000000000 to place your order");
        var entry = new CatalogEntry();
        reading.ApplyOffer(entry);

        Assert.Equal("MAs choice rice", reading.Name);
        Assert.Equal(CatalogKind.Food, reading.Kind);
        Assert.Equal([("50kg", 70000m), ("25kg", 35000m), ("12.5kg", 18500m), ("10kg", 14500m), ("5kg", 7500m)], Versions(entry));
    }

    [Fact]
    public void NoPrice_IsMissingAndHasNoVersions()
    {
        var entry = Item("THIS CHRISTMAS, GIVE THE GIFT OF FOOD!\n\nSurprise your parents or gift your staff with rice from us.\n\nWhatsApp: 08000000000\ndelivery nationwide");

        Assert.Empty(entry.Variants);
        Assert.True(PriceWords.For(entry).Missing);
    }

    [Fact]
    public void OneColour_StaysInTheWords_NotOnEachVersion()
    {
        var entry = Item("Introducing CHELSEA In this Gorgeous Grey\nPrice;\nUK size 6 - 12: 58,500\nUK size14- 20: 62,000");

        Assert.Equal([("UK 6–12", 58500m), ("UK 14–20", 62000m)], Versions(entry));
    }

    [Fact]
    public void HookLineFirst_NameComesFromTheIntroduction()
    {
        var caption = "BLACK but make it OUTSTANDING 🖤🖤🖤\n\nIntroducting QUEENCY💃💃💃\n\nWe are strictly a UK sizing brand…\n\nPrice; \nUK size 6 - 12: 50,500\nUK size14- 20: 50,500\nKindly send a dm";
        var entry = Item(caption);

        Assert.Equal("Queency", DemoCaptionReader.Read(caption).Name);
        Assert.Equal([("UK 6–12", 50500m), ("UK 14–20", 50500m)], Versions(entry));
        Assert.Equal("2 variants", PriceWords.For(entry).Detail);
    }

    [Fact]
    public void ColoursAtOnePrice_AreVersionsAtThatPrice()
    {
        var entry = Item("Silk scarf 15k\nColours: black, brown, wine");

        Assert.Equal([("Black", 15000m), ("Brown", 15000m), ("Wine", 15000m)], Versions(entry));
        Assert.Equal("3 variants", PriceWords.For(entry).Detail);
    }

    [Fact]
    public void PriceWords_SayHowManyVersions()
    {
        var entry = Item("Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k\nUk 14 yellow 64k");

        Assert.Equal("3 variants", PriceWords.For(entry).Detail);
        // Under the name: the category only; the variants are counted by the price.
        Assert.Equal("", PriceWords.Meta(Item("CHELSEA\nUK size 6 - 12: 58,500\nUK size14- 20: 62,000")));
    }

    [Fact]
    public void StartingPriceWithVariants_ShowsTheirRange()
    {
        var entry = new CatalogEntry
        {
            PriceType = PriceType.From, Price = 25000m,
            Options = [new ItemOption { Name = PriceWords.VariantList, Values = ["Trousers", "Two-piece", "Agbada"] }],
            Variants = [new ItemVariant { Key = "Trousers", Price = 25000m }, new ItemVariant { Key = "Two-piece", Price = 45000m }, new ItemVariant { Key = "Agbada", Price = 85000m }],
        };

        Assert.Equal("₦25,000 – ₦85,000", PriceWords.For(entry).Main);
        Assert.Equal("3 variants", PriceWords.For(entry).Detail);
    }

    [Fact]
    // Variants are available or sold out, never counted (designer, 29 October 2026): a version already on the item
    // keeps whether it's sold out (one counted to 0 is), and the item stops counting.
    public void UpdatingFromAPost_CopiesOnlyItsVersions_AndKeepsWhatIsSoldOut()
    {
        var match = new CatalogEntry
        {
            Name = "Ankara two-piece set", TracksStock = true,
            Options = [new ItemOption { Name = PriceWords.VariantList, Values = ["UK 6 Blue", "UK 12 Blue"] }],
            Variants = [new ItemVariant { Key = "UK 6 Blue", Price = 48000m, Stock = 0 }, new ItemVariant { Key = "UK 12 Blue", Price = 48000m, Stock = 2 }],
        };
        var from = Item("Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k\nUk 14 yellow 64k");

        var updated = PostDraft.Onto(match, from);

        Assert.Equal(["UK 6 Blue", "UK 12 Red", "UK 14 Yellow"], updated.Variants.Select(variant => variant.Key));
        Assert.False(updated.Variants[0].IsAvailable);
        Assert.True(updated.Variants[1].IsAvailable);
        Assert.All(updated.Variants, variant => Assert.Null(variant.Stock));
        Assert.False(updated.TracksStock);
        Assert.Equal(55000m, updated.Variants[1].Price);
    }

    [Fact]
    public void ACountedItem_GivenVersionsByAPost_StaysAvailable()
    {
        var match = new CatalogEntry { Name = "Ankara two-piece set", TracksStock = true, Stock = 5, Price = 48000m };
        var updated = PostDraft.Onto(match, Item("Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k"));

        Assert.False(updated.TracksStock);
        Assert.Null(updated.Stock);
        Assert.True(updated.AvailableNow);
    }
}
