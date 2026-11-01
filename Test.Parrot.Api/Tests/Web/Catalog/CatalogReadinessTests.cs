using System.Text.Json;
using Parrot.Web.Features.Dashboard.Catalog;
using Parrot.Web.Features.Dashboard.Catalog.ConceptE;

namespace Test.Parrot.Api.Tests.Web.Catalog;

public class CatalogReadinessTests
{
    private static CatalogEntry Complete() => new() { Name = "Scarf", Price = 12000, Status = CatalogStatus.Live };

    [Fact]
    public void OptionalDetailsAreNotRequired()
    {
        var item = Complete();
        Assert.Null(CatalogReadiness.Issue(item));
        Assert.True(item.AvailableNow);
    }

    [Fact]
    public void ACompleteDraftStillNeedsExplicitApproval()
    {
        var item = Complete();
        item.Status = CatalogStatus.Draft;
        Assert.Null(CatalogReadiness.Issue(item));
        Assert.True(CatalogReadiness.IsDraft(item));
        Assert.False(item.AvailableNow);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("kind")]
    [InlineData("price")]
    [InlineData("availability")]
    public void MissingBasicsCannotBeOfferedEvenOnOlderLiveRecords(string missing)
    {
        var item = Complete();
        switch (missing)
        {
            case "name": item.Name = " "; break;
            case "kind": item.Kind = (CatalogKind)999; break;
            case "price": item.Price = null; break;
            case "availability": item.AvailabilityIsUnconfirmed = true; break;
        }
        Assert.NotNull(CatalogReadiness.Issue(item));
        Assert.Equal(CatalogReadiness.Issue(item), CatalogReviewValidation.Issue(item));
        Assert.True(CatalogReadiness.IsDraft(item));
        Assert.False(item.AvailableNow);
    }

    [Fact]
    public void ExplicitPriceOnRequestNeedsNoAmount()
    {
        var item = Complete(); item.PriceType = PriceType.OnRequest; item.Price = null;
        Assert.Null(CatalogReadiness.Issue(item));
        Assert.True(item.AvailableNow);
    }

    [Fact]
    public void SoldOutIsACompleteItem_NotAnUnfinishedDraft()
    {
        var item = Complete(); item.IsAvailable = false;
        Assert.False(CatalogReadiness.IsDraft(item));
        Assert.False(item.AvailableNow);
    }

    [Fact]
    public void AvailableVariantsNeedPrices_AndAllSoldOutCannotHideMissingPricing()
    {
        var item = Complete(); item.Price = null;
        item.Options = [new() { Name = "Style", Values = ["Short", "Long"] }];
        item.Variants = [new() { Key = "Short", Price = 10000 }, new() { Key = "Long" }];
        Assert.NotNull(CatalogReadiness.Issue(item));
        item.Variants[1].IsAvailable = false;
        Assert.Null(CatalogReadiness.Issue(item));
        item.Variants[0].IsAvailable = false; item.Variants[0].Price = null;
        Assert.NotNull(CatalogReadiness.Issue(item));
    }

    [Theory]
    [InlineData("Style")]
    [InlineData("Size")]
    [InlineData("Variant")]
    public void EveryOptionModelDisplaysTheActualVariantCount(string option)
    {
        var item = Complete();
        item.Options = [new() { Name = option, Values = ["A", "B", "C", "D"] }];
        item.Variants = [.. item.Options[0].Values.Select((value, index) => new ItemVariant { Key = value, Price = 10000 + index * 1000 })];
        Assert.Equal("4 variants", PriceWords.For(item).Detail);
        item.Variants.ForEach(variant => variant.Price = item.Price);
        Assert.Equal("4 variants", PriceWords.For(item).Detail);
        item.PriceType = PriceType.OnRequest;
        Assert.Equal("4 variants", PriceWords.For(item).Detail);
        item.PriceType = PriceType.Fixed; item.Price = null;
        item.Variants.ForEach(variant => variant.Price = null);
        Assert.Equal("4 variants", PriceWords.For(item).Detail);
    }

    [Fact]
    public void CountOnlySavedVariants_NotAllPossibleCombinations()
    {
        var item = Complete();
        item.Options = [new() { Name = "Size", Values = ["S", "M"] }, new() { Name = "Colour", Values = ["Blue", "Red"] }];
        item.Variants = [new() { Key = "S / Blue" }, new() { Key = "M / Red" }];
        Assert.Equal("2 variants", PriceWords.For(item).Detail);
    }

    [Fact]
    public void UnfinishedEditorValuesSurviveSerializationAndAreDeepCopied()
    {
        var item = Complete(); item.Status = CatalogStatus.Draft;
        item.EditorDraft = new() { Pricing = "variants", Price = "unfinished", Stock = "-", Variants = [new("", "12k", false), new("Blue", "", true)] };
        var restored = JsonSerializer.Deserialize<CatalogEntry>(JsonSerializer.Serialize(item))!;
        Assert.Equal("unfinished", restored.EditorDraft!.Price);
        Assert.Equal("", restored.EditorDraft.Variants[0].Name);
        Assert.Equal("12k", restored.EditorDraft.Variants[0].Price);
        Assert.False(restored.AvailableNow);
        var copy = restored.Copy();
        copy.EditorDraft!.Variants.Clear();
        Assert.Equal(2, restored.EditorDraft.Variants.Count);
    }
}
