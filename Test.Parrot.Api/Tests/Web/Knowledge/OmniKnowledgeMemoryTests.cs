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

    // Writing waits on the service's rules for who may change knowledge, so the page offers nothing it can't do.
    [Fact]
    public void TheStoreReadsOnly_AndIsOnlyUsedWhenSwitchedOn()
    {
        Assert.False(new Fixture().Store.CanEdit);
        Assert.False(new Fixture().Store.CanReadLinks);
        Assert.True(new Fixture(on: true).Store.Enabled);
        Assert.False(new Fixture(on: false).Store.Enabled);
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
