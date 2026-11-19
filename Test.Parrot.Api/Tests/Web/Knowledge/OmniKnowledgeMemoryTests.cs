using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Knowledge;
using Parrot.Web.Services;
using Test.Parrot.Api.Tests.Web.AiAgents;

namespace Test.Parrot.Api.Tests.Web.Knowledge;

// The Knowledge page's view of the AI service's business knowledge (/api/v1/business-knowledge), read-only for now.
public class OmniKnowledgeMemoryTests
{
    private const string Items = "api/v1/business-knowledge/items";
    private const string Review = "api/v1/business-knowledge/review";

    [Fact]
    public async Task ListAsync_ListsEachPublishedItemUnderItsSectionsHeading_AsThePersonSignedIn()
    {
        Fixture f = new();
        f.Omni.On("GET", Items, """
            {"items": [
              {"id": "itm_1", "kind": "knowledge", "section": "about", "text": "We make handmade adire fabrics.", "state": "active",
               "sources": [{"origin": "manual", "label": "You told your AI"}], "version": 1,
               "created_at": "2026-11-14T10:00:00Z", "updated_at": "2026-11-14T10:00:00Z"},
              {"id": "itm_2", "kind": "fact", "section": "delivery", "text": "We deliver across Lagos in 1 to 2 days.", "state": "active",
               "sources": [{"origin": "channel", "label": "Found on your Instagram bio"}], "version": 2,
               "created_at": "2026-11-15T10:00:00Z", "updated_at": "2026-11-16T10:00:00Z"},
              {"id": "itm_3", "kind": "knowledge", "section": "product_care", "text": "Wash adire by hand in cold water.", "state": "active",
               "sources": [], "version": 1, "created_at": "2026-11-17T10:00:00Z", "updated_at": null}
            ], "next_cursor": null}
            """);
        f.Omni.On("GET", Review, """{"items": [], "next_cursor": null}""");

        KnowledgeSnapshot snapshot = await f.Store.ListAsync();

        Assert.All(snapshot.Facts, fact => Assert.Equal(FactState.Known, fact.State));
        Assert.Equal(["About the business", "Delivery", "Product care"], snapshot.Facts.Select(fact => fact.Label));
        Assert.Equal([KnowledgeTopic.Products, KnowledgeTopic.Delivery, KnowledgeTopic.Notes], snapshot.Facts.Select(fact => fact.Topic));
        KnowledgeFact delivery = snapshot.Facts.Single(fact => fact.Id == "itm_2");
        Assert.Equal("We deliver across Lagos in 1 to 2 days.", delivery.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), delivery.UpdatedAt);
        Assert.Equal("Found on your Instagram bio", delivery.Source);
        // What the owner wrote has no "found on" line.
        Assert.Null(snapshot.Facts.Single(fact => fact.Id == "itm_1").Source);
        // Never edited: its creation is when it was last updated.
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero), snapshot.Facts.Single(fact => fact.Id == "itm_3").UpdatedAt);
        Assert.Equal("Bearer session-token", f.Omni.Single("GET", Items).Header("Authorization"));
    }

    [Fact]
    public async Task ListAsync_TurnsEachPendingCardIntoAThingToCheck()
    {
        Fixture f = new();
        f.Omni.On("GET", Items, """{"items": [], "next_cursor": null}""");
        f.Omni.On("GET", Review, """
            {"items": [
              {"id": "card_found", "type": "found_detail", "state": "pending", "section": "delivery", "kind": "fact",
               "text": "Delivery in Lagos costs 2,000 naira.", "question": null, "confidence": 0.88,
               "sources": [{"origin": "channel", "label": "Found on your Instagram bio"}], "options": [],
               "asked_count": 0, "channels": [], "last_asked_at": null, "created_at": "2026-11-18T10:15:02Z", "gap_key": null},
              {"id": "card_question", "type": "customer_question", "state": "pending", "section": null, "kind": null,
               "text": null, "question": "Do you ship to Abuja?", "confidence": null, "sources": [], "options": [],
               "asked_count": 3, "channels": ["whatsapp"], "last_asked_at": "2026-11-18T09:00:00Z", "created_at": "2026-11-17T09:00:00Z", "gap_key": null},
              {"id": "card_conflict", "type": "disagreement", "state": "pending", "section": "delivery", "kind": null,
               "text": null, "question": "How much is delivery in Lagos?", "confidence": null, "sources": [],
               "options": [
                 {"option_id": "opt_a", "text": "2,000 naira", "source": {"origin": "channel", "label": "Found on your Instagram bio"}},
                 {"option_id": "opt_b", "text": "1,500 naira", "source": {"origin": "channel", "label": "Found on your website"}}],
               "asked_count": 0, "channels": [], "last_asked_at": null, "created_at": "2026-11-18T10:00:00Z", "gap_key": null},
              {"id": "card_gap", "type": "gap_ask", "state": "pending", "section": null, "kind": null, "text": null,
               "question": "Do you offer gift wrapping?", "confidence": null, "sources": [], "options": [],
               "asked_count": 4, "channels": [], "last_asked_at": null, "created_at": "2026-11-18T09:00:00Z", "gap_key": "offers:gift-wrapping"}
            ], "next_cursor": null}
            """);

        KnowledgeSnapshot snapshot = await f.Store.ListAsync();

        KnowledgeFact found = Assert.Single(snapshot.Facts);
        Assert.Equal(("card_found", FactState.ToConfirm, "Delivery", "Found on your Instagram bio"), (found.Id, found.State, found.Label, found.Source));
        Assert.Equal(["Do you ship to Abuja?", "Do you offer gift wrapping?"], snapshot.Questions.Select(question => question.Question));
        KnowledgeQuestion asked = snapshot.Questions.Single(question => question.Id == "card_question");
        Assert.Equal((3, "whatsapp"), (asked.Asked, asked.Channel));
        KnowledgeConflict conflict = Assert.Single(snapshot.Conflicts);
        Assert.Equal("How much is delivery in Lagos?", conflict.Question);
        Assert.Equal(["2,000 naira", "1,500 naira"], conflict.Claims.Select(claim => claim.Text));
        Assert.Equal("Found on your website", conflict.Claims[1].Source);
    }

    [Fact]
    public async Task ListAsync_WhenKnowledgeIsOffOnTheService_FailsWithItsErrorCode()
    {
        Fixture f = new();
        f.Omni.On("GET", Items,
            """{"success": false, "errorCode": "KNOWLEDGE_UNAVAILABLE", "message": "Business knowledge is not available.", "traceId": "t1"}""",
            HttpStatusCode.ServiceUnavailable);

        ApiException error = await Assert.ThrowsAsync<ApiException>(() => f.Store.ListAsync());

        Assert.Equal(OmniKnowledgeMemory.KnowledgeUnavailable, error.ErrorCode);
    }

    // Telling and rewriting come first; deciding the things to check and reading links come in later phases,
    // so the page offers nothing the store can't do yet.
    [Fact]
    public void TheStoreTakesEditsButNotYetDecisions_AndIsOnlyUsedWhenSwitchedOn()
    {
        Assert.True(new Fixture().Store.CanEdit);
        Assert.False(new Fixture().Store.CanReview);
        Assert.False(new Fixture().Store.CanReadLinks);
        Assert.True(new Fixture(on: true).Store.Enabled);
        Assert.False(new Fixture(on: false).Store.Enabled);
    }

    // ── Telling the AI something ──

    private const string AddedDelivery = """
        {"items": [{"id": "itm_new", "kind": "knowledge", "section": "delivery", "text": "We deliver across Lagos in 1 to 2 days.",
          "state": "active", "sources": [{"origin": "manual", "label": "You told your AI"}], "version": 1,
          "created_at": "2026-11-19T10:00:00Z", "updated_at": "2026-11-19T10:00:00Z"}], "replaced": []}
        """;

    // The service files everything in one request under one section, so each sentence goes on its own and
    // the service picks the section for each.
    [Fact]
    public async Task AddAsync_SendsEachStatementOnItsOwn_LeavingTheSectionToTheService()
    {
        Fixture f = new();
        f.Omni.On("POST", Items, AddedDelivery, HttpStatusCode.Created);

        IReadOnlyList<KnowledgeFact> saved = await f.Store.AddAsync("We deliver across Lagos in 1 to 2 days.  Customers pay by bank transfer.\n");

        Assert.Equal(
            ["""{"text":"We deliver across Lagos in 1 to 2 days."}""", """{"text":"Customers pay by bank transfer."}"""],
            f.Omni.Requests.Where(r => r.Method == "POST").Select(r => r.Body));
        Assert.Equal(2, saved.Count);
        Assert.All(saved, fact => Assert.Equal(("Delivery", FactState.Known), (fact.Label, fact.State)));
    }

    [Fact]
    public async Task AddAsync_WhenTheAIAlreadyKnowsIt_SavesNothing()
    {
        Fixture f = new();
        f.Omni.On("POST", Items, """{"items": [], "replaced": []}""", HttpStatusCode.Created);

        Assert.Empty(await f.Store.AddAsync("We deliver across Lagos in 1 to 2 days."));
    }

    // A statement the addition replaced is retired, and the service can't restore a retired one, so Undo writes it back.
    [Fact]
    public async Task UndoAddAsync_RemovesWhatWasAdded_AndWritesBackWhatItReplaced()
    {
        Fixture f = new();
        f.Omni.On("GET", Items, """
            {"items": [{"id": "itm_old", "kind": "knowledge", "section": "delivery", "text": "Delivery in Lagos costs 2,000 naira.",
              "state": "active", "sources": [], "version": 1, "created_at": "2026-11-14T10:00:00Z", "updated_at": null}], "next_cursor": null}
            """);
        f.Omni.On("GET", Review, """{"items": [], "next_cursor": null}""");
        f.Omni.On("POST", Items, AddedDelivery.Replace("\"replaced\": []", "\"replaced\": [\"itm_old\"]"), HttpStatusCode.Created);
        f.Omni.On("DELETE", $"{Items}/itm_new", "", HttpStatusCode.NoContent);
        await f.Store.ListAsync();
        IReadOnlyList<KnowledgeFact> saved = await f.Store.AddAsync("Delivery in Lagos costs 2,500 naira.");

        await f.Store.UndoAddAsync(saved);

        f.Omni.Single("DELETE", $"{Items}/itm_new");
        Assert.Equal("""{"text":"Delivery in Lagos costs 2,000 naira.","section":"delivery"}""",
            f.Omni.Requests.Where(r => r.Method == "POST").Last().Body);
    }

    [Fact]
    public async Task UndoAddAsync_WhenTheItemIsAlreadyGone_CountsItAsUndone()
    {
        Fixture f = new();
        f.Omni.On("DELETE", $"{Items}/itm_new",
            """{"success": false, "errorCode": "ITEM_NOT_FOUND", "message": "No such item.", "traceId": "t1"}""", HttpStatusCode.NotFound);

        await f.Store.UndoAddAsync([new KnowledgeFact("itm_new", KnowledgeTopic.Delivery, "x", FactState.Known, null, DateTimeOffset.UtcNow, "Delivery")]);
    }

    [Fact]
    public async Task AddAsync_WhenThePersonMayNotChangeKnowledge_IsRefusedInPlainWords()
    {
        Fixture f = new();
        f.Omni.On("POST", Items,
            """{"success": false, "errorCode": "FORBIDDEN", "message": "Your role can't change business knowledge.", "traceId": "t1"}""",
            HttpStatusCode.Forbidden);

        KnowledgeSaveRefusedException refused = await Assert.ThrowsAsync<KnowledgeSaveRefusedException>(() => f.Store.AddAsync("We open at 9."));

        Assert.Equal("You don't have permission to change what your AI knows. Ask the workspace owner.", refused.Message);
        Assert.False(refused.SectionChanged);
    }

    // ── Rewriting a section ──

    private const string DeliverySection = "api/v1/business-knowledge/sections/delivery";

    private static Fixture WithDeliverySection(int itemCount = 2)
    {
        Fixture f = new();
        f.Omni.On("GET", DeliverySection, $$"""
            {"key": "delivery", "label": "Delivery", "summary": null, "item_count": {{itemCount}}, "updated_at": "2026-11-18T10:20:00Z", "version": 3,
             "items": [
               {"id": "itm_k", "kind": "knowledge", "section": "delivery", "text": "Delivery in Lagos takes 1 to 2 days.", "state": "active", "sources": [], "version": 1},
               {"id": "itm_f", "kind": "fact", "section": "delivery", "text": "Delivery in Lagos costs 2,000 naira.", "state": "active", "sources": [], "version": 1}]}
            """);
        f.Omni.On("PUT", DeliverySection, """{"key": "delivery", "label": "Delivery", "version": 4, "items": [], "changes": {"kept": ["itm_k"], "added": [], "retired": []}}""");
        f.Omni.On("DELETE", $"{Items}/itm_f", "", HttpStatusCode.NoContent);
        return f;
    }

    [Fact]
    public async Task OpenSectionAsync_GivesItsDetailsOnePerLine_AndTheVersionTheyWereReadAt()
    {
        Fixture f = WithDeliverySection();

        KnowledgeSectionDraft draft = await f.Store.OpenSectionAsync("Delivery");

        Assert.Equal("Delivery in Lagos takes 1 to 2 days.\nDelivery in Lagos costs 2,000 naira.", draft.Text);
        Assert.Equal(3, draft.Version);
        Assert.Equal([true, false], draft.Lines.Select(line => line.Rewritable));
    }

    // Knowledge switched off on the service is said as it is, not as a lost connection.
    [Fact]
    public async Task OpenSectionAsync_WhenKnowledgeIsOffOnTheService_SaysSoInPlainWords()
    {
        Fixture f = new();
        f.Omni.On("GET", DeliverySection,
            """{"success": false, "errorCode": "KNOWLEDGE_UNAVAILABLE", "message": "Business knowledge is not available.", "traceId": "t1"}""",
            HttpStatusCode.ServiceUnavailable);

        KnowledgeSaveRefusedException refused = await Assert.ThrowsAsync<KnowledgeSaveRefusedException>(() => f.Store.OpenSectionAsync("Delivery"));

        Assert.Equal("Your AI's knowledge can't be changed right now. Try again in a minute.", refused.Message);
    }

    // Rewriting from a partial list would remove what wasn't shown.
    [Fact]
    public async Task OpenSectionAsync_WhenTheSectionHoldsMoreThanTheServiceSends_RefusesToEditIt()
    {
        Fixture f = WithDeliverySection(itemCount: 250);

        await Assert.ThrowsAsync<KnowledgeSaveRefusedException>(() => f.Store.OpenSectionAsync("Delivery"));
    }

    [Fact]
    public async Task SaveSectionAsync_RewritesTheStatementsAgainstTheOpenedVersion_AndRemovesAFactWhoseLineIsGone()
    {
        Fixture f = WithDeliverySection();
        KnowledgeSectionDraft draft = await f.Store.OpenSectionAsync("Delivery");

        await f.Store.SaveSectionAsync(draft, "Delivery in Lagos takes 1 to 2 days.\n\nWe ship abroad on request.");

        Recorded rewrite = f.Omni.Single("PUT", DeliverySection);
        Assert.Equal("""{"text":"Delivery in Lagos takes 1 to 2 days.\nWe ship abroad on request."}""", rewrite.Body);
        Assert.Equal("3", rewrite.Header("If-Match"));
        f.Omni.Single("DELETE", $"{Items}/itm_f");
    }

    // A fact is kept as a fact: its line is not written again as a statement, and it is not removed.
    [Fact]
    public async Task SaveSectionAsync_KeepsAFactWhoseLineStays_OutOfTheRewrite()
    {
        Fixture f = WithDeliverySection();
        KnowledgeSectionDraft draft = await f.Store.OpenSectionAsync("Delivery");

        await f.Store.SaveSectionAsync(draft, "Delivery in Lagos takes 1 to 3 days.\nDelivery in Lagos costs 2,000 naira.");

        Assert.Equal("""{"text":"Delivery in Lagos takes 1 to 3 days."}""", f.Omni.Single("PUT", DeliverySection).Body);
        Assert.DoesNotContain(f.Omni.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task SaveSectionAsync_WhenSomeoneElseChangedTheSectionFirst_IsRefusedAndRemovesNothing()
    {
        Fixture f = WithDeliverySection();
        f.Omni.On("PUT", DeliverySection,
            """{"success": false, "errorCode": "VERSION_MISMATCH", "message": "The section has changed.", "traceId": "t1"}""",
            HttpStatusCode.PreconditionFailed);
        KnowledgeSectionDraft draft = await f.Store.OpenSectionAsync("Delivery");

        KnowledgeSaveRefusedException refused = await Assert.ThrowsAsync<KnowledgeSaveRefusedException>(
            () => f.Store.SaveSectionAsync(draft, "We ship abroad on request."));

        Assert.True(refused.SectionChanged);
        Assert.DoesNotContain(f.Omni.Requests, r => r.Method == "DELETE");
    }

    // The browser preview edits the same way: a kept line keeps its fact, a new one is added, one taken out goes.
    [Fact]
    public async Task ThePreview_SavesASectionLineByLine()
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "session-token", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        KnowledgePreview preview = new(new ConfigurationBuilder().Build(), new BrowserStorage(), auth);
        await preview.AddAsync("We deliver across Lagos in 1 to 2 days. We deliver to Abuja on Fridays.");
        KnowledgeSectionDraft draft = await preview.OpenSectionAsync("Delivery");
        string kept = draft.Lines[0].Id;

        await preview.SaveSectionAsync(draft, "We deliver across Lagos in 1 to 2 days.\nWe ship abroad on request.");

        IReadOnlyList<KnowledgeFact> delivery = [.. (await preview.ListAsync()).Facts.Where(fact => fact.Label == "Delivery")];
        Assert.Equal(["We deliver across Lagos in 1 to 2 days.", "We ship abroad on request."], delivery.Select(fact => fact.Text));
        Assert.Equal(kept, delivery[0].Id);
    }

    private sealed class BrowserStorage : Microsoft.JSInterop.IJSRuntime
    {
        private readonly Dictionary<string, string> _items = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.setItem") _items[(string)args![0]!] = (string)args[1]!;
            object? value = identifier == "localStorage.getItem" ? _items.GetValueOrDefault((string)args![0]!) : null;
            return ValueTask.FromResult((TValue)value!);
        }
    }

    // A switch left empty (OmniAi__Knowledge=) or mistyped must mean off, never stop the web app from starting.
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void IsOn_ReadsAMissingEmptyOrMistypedSwitchAsOff(string? value, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OmniAi:Knowledge"] = value })
            .Build();

        Assert.Equal(expected, OmniKnowledgeMemory.IsOn(configuration, "OmniAi:Knowledge"));
    }

    private sealed class Fixture
    {
        public Routes Omni { get; } = new();
        public OmniKnowledgeMemory Store { get; }

        public Fixture(bool on = true)
        {
            AuthState auth = new();
            auth.SetUser(new AuthResponse { Token = "session-token", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["OmniAi:Knowledge"] = on ? "true" : "false" })
                .Build();
            Store = new OmniKnowledgeMemory(new OmniAiClient(new HttpClient(Omni) { BaseAddress = new Uri("http://omni.test/") }, auth), configuration,
                NullLogger<OmniKnowledgeMemory>.Instance);
        }
    }
}
