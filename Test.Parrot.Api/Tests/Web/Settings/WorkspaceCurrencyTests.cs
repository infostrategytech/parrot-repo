using System.Reflection;
using System.Text;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.Catalog.ConceptE;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Catalog;
using Parrot.Web.Features.Dashboard.Catalog.ConceptE;
using Parrot.Web.Features.Settings;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class WorkspaceCurrencyTests
{
    [Theory]
    [InlineData("NGN", "₦1,250.75")]
    [InlineData("USD", "$1,250.75")]
    [InlineData("GBP", "£1,250.75")]
    [InlineData("GHS", "GH₵1,250.75")]
    [InlineData("EUR", "€1,250.75")]
    [InlineData("xyz", "XYZ 1,250.75")]
    public void FormatUsesTheGivenCurrencyWithoutConvertingTheNumber(string code, string expected)
        => Assert.Equal(expected, CurrencyAmount.Format(1250.75m, code));

    [Fact]
    public async Task SavedDefaultAffectsNewEntriesButNotSavedOrLegacyPrices()
    {
        var auth = Auth("workspace-a"); var store = new Store(); var currency = new WorkspaceCurrency(store, auth);
        await currency.EnsureAsync(); Assert.Equal("NGN", currency.Current.Code);
        currency.Saved(BusinessDetails.Defaults with { Currency = "USD" }, currency.WorkspaceKey);
        var added = Form(currency.Current).CaptureDraft(); Assert.Equal("USD", added.CurrencyCode);
        added.Price = 100; Assert.Equal("$100", PriceWords.For(added).Main);
        currency.Saved(BusinessDetails.Defaults with { Currency = "GBP" }, currency.WorkspaceKey);
        Assert.Equal("USD", added.Copy().CurrencyCode); Assert.Equal("$100", PriceWords.For(added).Main);
        Assert.Equal("GBP", Form(currency.Current).CaptureDraft().CurrencyCode);
        var legacy = new CatalogEntry { Id = "existing", Price = 50000 };
        Assert.Equal("₦50,000", PriceWords.For(Form(currency.Current, legacy).CaptureDraft()).Main);
    }

    [Fact]
    public void InProgressAndRestoredDraftsKeepTheCurrencyTheyStartedWith()
    {
        var form = Form(new("USD"));
        var captured = form.CaptureState();
        form.Currency = new("GBP"); Call(form, "OnParametersSet");
        Assert.Equal("USD", form.CaptureDraft().CurrencyCode);
        var restored = new ItemForm { Currency = new("GBP"), RestoredDraft = captured }; Call(restored, "OnInitialized");
        Assert.Equal("USD", restored.CaptureDraft().CurrencyCode);
    }

    [Fact]
    public void LoadingTheDefaultDoesNotMakeAnUntouchedEditorDirty()
    {
        var form = Form(new("NGN", Ready: false)); Assert.False(form.HasChanges);
        form.Currency = new("USD"); Call(form, "OnParametersSet");
        Assert.False(form.HasChanges); Assert.Equal("USD", form.CaptureDraft().CurrencyCode);
    }

    [Fact]
    public async Task WorkspaceSwitchLoadsItsOwnDefaultAndDiscardsALateOldRead()
    {
        var auth = Auth("a"); var late = new TaskCompletionSource<BusinessDetails>();
        var store = new Store { Read = () => late.Task }; var state = new WorkspaceCurrency(store, auth);
        var first = state.EnsureAsync(); Assert.False(state.Current.Ready);
        auth.SetUser(User("b")); store.Read = () => Task.FromResult(BusinessDetails.Defaults with { Currency = "GHS" });
        await state.EnsureAsync(); Assert.Equal("GHS", state.Current.Code);
        late.SetResult(BusinessDetails.Defaults with { Currency = "USD" }); await first;
        Assert.Equal("GHS", state.Current.Code);
        state.Saved(BusinessDetails.Defaults with { Currency = "GBP" }, "controlled:a"); Assert.Equal("GHS", state.Current.Code);
    }

    [Fact]
    public async Task FailedReadNeverFallsBackToAnEditableWrongCurrencyAndCanRetry()
    {
        var store = new Store { Read = () => Task.FromException<BusinessDetails>(new HttpRequestException("Controlled offline")) };
        var state = new WorkspaceCurrency(store, Auth("a")); await state.EnsureAsync();
        Assert.False(state.Current.Ready); Assert.NotNull(state.Current.Problem);
        var form = Form(state.Current); Assert.Null(form.CaptureDraft().CurrencyCode);
        store.Read = () => Task.FromResult(BusinessDetails.Defaults with { Currency = "GBP" });
        await state.EnsureAsync(retry: true); Assert.True(state.Current.Ready); Assert.Equal("GBP", state.Current.Code);
    }

    [Theory]
    [InlineData("Kaftan - $25.50", "USD")]
    [InlineData("Kaftan - £25.50", "GBP")]
    [InlineData("Kaftan - GH₵25.50", "GHS")]
    [InlineData("Kaftan - ₦25.50", "NGN")]
    public void ImportedPricesKeepTheirExplicitCurrency(string text, string code)
    {
        var line = Assert.Single(PriceListParser.ParseText(text));
        Assert.Equal(25.50m, line.Price); Assert.Equal(code, line.CurrencyCode);
        Assert.Equal(code, ReviewDraft.FromLine(line, 0, []).Item.CurrencyCode);
    }

    [Fact]
    public void SpreadsheetColumnCurrencyIsPreservedAndDifferentCurrenciesNeverUpdateEachOther()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("Item,Price (USD)\nKaftan,25.50"));
        var sheet = PriceListParser.ParseSpreadsheet(file, "controlled.csv");
        var line = Assert.Single(sheet.Lines(sheet.NameColumn, sheet.PriceColumn)); Assert.Equal("USD", line.CurrencyCode);
        var existing = new CatalogEntry { Name = "Kaftan", Price = 50000, CurrencyCode = "NGN" };
        var review = ReviewDraft.FromLine(line, 0, [existing]); Assert.Null(review.Match); Assert.False(review.Update);
        Assert.Throws<InvalidOperationException>(() => PostDraft.Onto(existing, review.Item)); Assert.Equal(50000, existing.Price);
    }

    [Fact]
    public void RealTaskAndAgentAdaptersNeverDropUnsupportedCurrencyFieldsSilently()
    {
        // Amounts in another currency are refused rather than saved as naira; a task with no amounts has none to lose.
        var method = typeof(ApiTaskStore).GetMethod("ToRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [new TaskDraft(TaskKind.Other, "Controlled", Total: 25m, CurrencyCode: "USD")]));
        Assert.IsType<ApiException>(error.InnerException);
        Assert.NotNull(method.Invoke(null, [new TaskDraft(TaskKind.Other, "Controlled", CurrencyCode: "USD")]));
        // Parrot's own agents API keeps naira only; the AI service is sent the currency (agents-backend-requests).
        var dollars = new AgentSettings { HandOverBigOrders = true, BigOrderCurrency = "USD", BigOrderNaira = 100 };
        Assert.Throws<ApiException>(() => AgentMapping.ToInfo(new AiAgent { Name = "Controlled", Settings = dollars }));
        Assert.Equal("USD", OmniAgentMapping.ToSettings(dollars).BigOrderCurrency);
        Assert.Null(OmniAgentMapping.ToSettings(new AgentSettings { HandOverBigOrders = true }).BigOrderCurrency);
        Assert.NotNull(AgentMapping.ToDto(new AgentSettings { HandOverBigOrders = true }));
    }

    [Theory]
    [InlineData("USD", 25.5, 28.05)]
    [InlineData("GBP", 1.25, 1.38)]
    [InlineData("NGN", 5000, 5500)]
    public void BulkPercentageRoundingRespectsCurrencyInsteadOfRoundingDollarsToHundreds(string currency, double before, double expected)
    {
        var panel = new ChangePrices(); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(ChangePrices).GetField("_amount", flags)!.SetValue(panel, "10");
        Assert.Equal((decimal)expected, typeof(ChangePrices).GetMethod("Move", flags)!.Invoke(panel, [(decimal)before, currency]));
    }

    [Theory]
    [InlineData("Kaftan $25.50", "USD")]
    [InlineData("Kaftan £25.50", "GBP")]
    [InlineData("Kaftan GH₵25.50", "GHS")]
    public void SocialPostExtractionKeepsTheSourcesCurrencyEvenWhenTheDefaultDiffers(string caption, string currency)
    {
        var post = new SocialPost("controlled", "instagram", "", caption, "", "");
        var draft = PostDraft.From(post, DemoCaptionReader.Read(caption), [], "NGN");
        Assert.Equal(25.50m, draft.Item.Price); Assert.Equal(currency, draft.Item.CurrencyCode);
    }

    [Fact]
    public void AMixedCurrencySourceRequiresAPriceInsteadOfGuessingOrConverting()
    {
        const string caption = "Kaftan $25 or ₦50,000";
        var post = new SocialPost("controlled", "instagram", "", caption, "", "");
        var draft = PostDraft.From(post, DemoCaptionReader.Read(caption), [], "GBP");
        Assert.Null(draft.Item.Price); Assert.Empty(draft.Item.Variants); Assert.Equal("GBP", draft.Item.CurrencyCode);
        Assert.Contains(draft.Checks, check => check.Contains("different currencies"));
        var line = Assert.Single(PriceListParser.ParseText(caption)); Assert.Null(line.Price); Assert.True(line.NeedsCheck);
    }

    private static AuthResponse User(string workspace) => new() { UserId = "controlled", WorkspaceId = workspace, Token = "controlled", ExpiresAt = DateTime.UtcNow.AddHours(1) };
    private static AuthState Auth(string workspace) { var auth = new AuthState(); auth.SetUser(User(workspace)); return auth; }
    private static ItemForm Form(WorkspaceCurrencyValue currency, CatalogEntry? item = null)
    { var form = new ItemForm { Currency = currency, Item = item }; Call(form, "OnInitialized"); return form; }
    private static void Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, null);
    private sealed class Store : IBusinessDetailsStore
    {
        public bool Available => true;
        public Func<Task<BusinessDetails>> Read = () => Task.FromResult(BusinessDetails.Defaults);
        public Task<BusinessDetails> ReadAsync(CancellationToken ct = default) => Read();
        public Task<BusinessDetails> SaveAsync(BusinessDetails details, CancellationToken ct = default) => Task.FromResult(details);
    }
}
