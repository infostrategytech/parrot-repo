using System.Reflection;
using System.Text;
using System.Text.Json;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.Catalog.ConceptE;
using Parrot.Web.Features.Dashboard.Catalog;

namespace Test.Parrot.Api.Tests.Web.Catalog;

public class ConceptEEntryTests
{
    [Theory]
    [InlineData("-500")]
    [InlineData("500abc")]
    [InlineData("abc500")]
    [InlineData("1e3")]
    [InlineData("12k")]
    [InlineData("12.345")]
    [InlineData("12,34")]
    [InlineData("1.2.3")]
    public void TypedAmountsMustBeWholeValidNumbers_NotCaptionExtractions(string value) => Assert.Null(NumberText.Decimal(value));

    [Fact]
    public void ValidNumbersRetainTheirValue()
    {
        Assert.Equal(.5m, NumberText.Decimal(".5"));
        Assert.Equal(12000.25m, NumberText.Decimal("12,000.25"));
        Assert.Equal("0.5", NumberText.Grouped(".5"));
        Assert.Equal("-500", NumberText.Grouped("-500"));
        Assert.Equal("12.345", NumberText.Grouped("12.345"));
        Assert.Equal(0, NumberText.Integer("0", 999999));
        Assert.Null(NumberText.Integer("1e3"));
        Assert.Null(NumberText.Integer("1.5"));
        Assert.Null(NumberText.Integer("-1"));
        Assert.Null(NumberText.Integer("1000000", 999999));
    }

    [Fact]
    public void SpreadsheetRejectsInvalidNumbers_ButCanRecogniseExplicitImportShorthand()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Item,Price\nA,-500\nB,500abc\nC,1e3\nD,\"1,200.25\"\nE,12k"));
        var sheet = PriceListParser.ParseSpreadsheet(stream, "items.csv");
        var lines = sheet.Lines(sheet.NameColumn, sheet.PriceColumn);
        Assert.Equal(new decimal?[] { null, null, null, 1200.25m, 12000m }, lines.Select(line => line.Price));
    }

    // Variants are available or sold out, never counted (designer, 29 October 2026): an older item's counts become
    // just that, and nothing else about it changes.
    [Fact]
    public void MetadataEditPreservesLegacyOptions_StartingPrice_AndWhatIsSoldOut()
    {
        var item = Legacy();
        var form = new ItemForm { Item = item }; Init(form);
        Set(form, "_name", "Updated name");
        var result = form.CaptureDraft();
        Assert.Equal(PriceType.From, result.PriceType);
        Assert.Equal("per yard", result.PriceUnit);
        Assert.Equal(item.Price, result.Price);
        Assert.Equal(JsonSerializer.Serialize(item.Options), JsonSerializer.Serialize(result.Options));
        Assert.Equal(["S", "M"], result.Variants.Select(variant => variant.Key));
        Assert.True(result.Variants[0].IsAvailable);
        Assert.False(result.Variants[1].IsAvailable);
        Assert.All(result.Variants, variant => Assert.Null(variant.Stock));
        Assert.False(result.TracksStock);
        Assert.Equal("Updated name", result.Name);
    }

    [Fact]
    public void LegacyVariantPricesChange_AndCountsBecomeAvailableOrSoldOut()
    {
        var item = Legacy(); var first = new ItemForm { Item = item }; Init(first);
        var values = first.CaptureState();
        values.Versions[0] = values.Versions[0] with { Price = "1500" };
        var edit = new ItemForm { Item = item, RestoredDraft = values }; Init(edit);
        var result = edit.CaptureDraft();
        Assert.Equal(1500m, result.Variants[0].Price);
        Assert.True(result.Variants[0].IsAvailable);
        Assert.False(result.Variants[1].IsAvailable);
        Assert.All(result.Variants, variant => Assert.Null(variant.Stock));
        Assert.Equal("Size", result.Options[0].Name);
        Assert.False(result.TracksStock);
    }

    [Fact]
    public void AVariant_HasNoStockField()
    {
        var form = new ItemForm { Item = Legacy() }; Init(form);
        Assert.Null(typeof(ItemForm).GetField("_legacyStockTracking", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(ItemForm).GetField("_variantStocks", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public async Task SaveCannotInterpretNegativeInputAsAPositivePrice()
    {
        var item = new CatalogEntry { Name = "Test", Price = 500 };
        var calls = 0;
        var form = new ItemForm { Item = item, Save = (_, _) => { calls++; return Task.FromResult<string?>(null); } }; Init(form);
        Set(form, "_price", "-500");
        await Act(form, "SaveAsync");
        Assert.Equal(0, calls);
        Assert.NotNull(Field<string?>(form, "_error"));
    }

    [Fact]
    public async Task BulkReductionCannotSilentlySkipASelectedPrice()
    {
        var calls = 0;
        var bulk = new ChangePrices
        {
            Items = [new() { Id = "low", Name = "Low", Price = 500 }, new() { Id = "high", Name = "High", Price = 2000 }],
            Apply = _ => { calls++; return Task.FromResult<string?>(null); },
        };
        Set(bulk, "_amount", "1000"); Set(bulk, "_unit", "naira"); Set(bulk, "_direction", "down");
        await Act(bulk, "ApplyAsync");
        Assert.Equal(0, calls);
        Set(bulk, "_amount", "100");
        await Act(bulk, "ApplyAsync");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BulkSaveFailureKeepsTheEditorOpen()
    {
        var bulk = new ChangePrices { Items = [new() { Name = "Item", Price = 1000 }], Apply = _ => throw new HttpRequestException("offline") };
        Set(bulk, "_open", true); Set(bulk, "_amount", "10");
        await Act(bulk, "ApplyAsync");
        Assert.True(Field<bool>(bulk, "_open"));
        Assert.NotNull(Field<string?>(bulk, "_error"));
    }

    [Fact]
    public async Task UnsavedManualWorkRequiresAnExplicitLeaveDecision()
    {
        var form = new ItemForm(); Init(form);
        Set(form, "_name", "Unfinished item");
        Assert.True(form.HasChanges);
        await Act(form, "BackAsync");
        Assert.True(Field<bool>(form, "_leaving"));
        typeof(ItemForm).GetMethod("KeepEditing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Assert.False(Field<bool>(form, "_leaving"));
        Assert.Equal("Unfinished item", form.CaptureState().Name);
    }

    private static CatalogEntry Legacy() => new()
    {
        Id = "legacy", Name = "Existing", PriceType = PriceType.From, Price = 1000, PriceUnit = "per yard", TracksStock = true,
        Options = [new() { Name = "Size", Values = ["S", "M"] }],
        Variants = [new() { Key = "S", Stock = 4 }, new() { Key = "M", Stock = 0 }],
    };
    private static void Init(ItemForm form) => typeof(ItemForm).GetMethod("OnInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Task Act(object target, string name) => (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;
}
