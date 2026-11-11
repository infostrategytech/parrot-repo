using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Events;
using Parrot.Catalog.Infrastructure.Services;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

/// <summary>
/// The AI team reports on catalog.ingestion-report how far it got with each item message.
/// An item shows "updating" from a save until the report about that save arrives.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class AiStatusTests
{
    private static readonly string[] Owner = [CatalogScopes.Read, CatalogScopes.Write, CatalogScopes.Publish];

    private readonly CatalogApiFixture _fx;

    public AiStatusTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task An_item_shows_updating_until_the_ai_reports_on_its_latest_save()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        HttpResponseMessage uploaded = await owner.PostAsync("/api/catalog/items/photos", Photo());
        ItemPhoto photo = await CatalogApiFixture.ReadAsync<ItemPhoto>(uploaded);

        object Form(int amount, string status = "published") => new
        {
            name = "Adire scarf",
            kind = "product",
            pricing = new { mode = "single", amount },
            availability = new { mode = "in_stock" },
            status,
            photos = new[] { photo.Key },
        };

        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Body(Form(5000))));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);

        (Guid firstSave, Guid mediaId, long firstVersion) = await LatestItemEventAsync(workspace, item.Id);
        DateTime at = DateTime.UtcNow;

        // The AI rejects the photo: the business is asked to look at it, with the AI's reason and what it saw.
        Assert.True(await HandleAsync(Report(workspace, item.Id, firstSave, at, PhotoResult(mediaId, "rejected", "client_err", "The photo shows a cartoon chicken, not an adire scarf."))));
        ItemAi ai = (await GetAsync(owner, item.Id)).Ai!;
        Assert.Equal(ItemAiState.NeedsAttention, ai.State);
        Assert.True(ai.Searchable);
        ItemAiPhoto rejected = Assert.Single(ai.Photos);
        Assert.Equal(photo.Key, rejected.PhotoKey);
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("client_err", rejected.ReasonCode);
        Assert.Equal("The photo shows a cartoon chicken, not an adire scarf.", rejected.Reason);
        Assert.Equal("A cartoon chicken.", rejected.Caption);

        // The same report delivered twice is recorded once; an older resend of this save is ignored.
        Assert.False(await HandleAsync(Report(workspace, item.Id, firstSave, at, PhotoResult(mediaId, "rejected", "client_err", "again"), reportId: _lastReportId)));
        Assert.False(await HandleAsync(Report(workspace, item.Id, firstSave, at.AddMinutes(-5), PhotoResult(mediaId, "embedded", null, null))));
        Assert.Equal(ItemAiState.NeedsAttention, (await GetAsync(owner, item.Id)).Ai!.State);

        // A new save: updating again, with a higher version, and a late report about the first save doesn't change that.
        await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Body(Form(6000)));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);
        Assert.False(await HandleAsync(Report(workspace, item.Id, firstSave, at.AddMinutes(1), PhotoResult(mediaId, "embedded", null, null))));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);

        (Guid secondSave, _, long secondVersion) = await LatestItemEventAsync(workspace, item.Id);
        Assert.True(secondVersion > firstVersion);

        // A report naming another workspace, or saying the save changed nothing, is ignored.
        Assert.False(await HandleAsync(Report(Guid.NewGuid(), item.Id, secondSave, at.AddMinutes(2), PhotoResult(mediaId, "embedded", null, null))));
        Assert.False(await HandleAsync(Report(workspace, item.Id, secondSave, at.AddMinutes(2), PhotoResult(mediaId, "embedded", null, null), status: "superseded")));

        // The report about the latest save: the AI has it, and says what it saw.
        Assert.True(await HandleAsync(Report(workspace, item.Id, secondSave, at.AddMinutes(3), PhotoResult(mediaId, "embedded", null, null))));
        ai = (await GetAsync(owner, item.Id)).Ai!;
        Assert.Equal(ItemAiState.Ready, ai.State);
        Assert.Equal("embedded", ai.TextStatus);
        ItemAiPhoto learned = Assert.Single(ai.Photos);
        Assert.Equal("embedded", learned.Status);
        Assert.Equal("A blue and pink patterned scarf.", learned.Caption);
        Assert.NotNull(ai.ReportedAt);

        // Back to draft: the AI takes it out of search.
        await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Body(Form(6000, "draft")));
        (Guid thirdSave, _, _) = await LatestItemEventAsync(workspace, item.Id);
        Assert.True(await HandleAsync(Report(workspace, item.Id, thirdSave, at.AddMinutes(4), PhotoResult(mediaId, "embedded", null, null), type: "ItemUnindexed", status: "unlisted")));
        Assert.Equal(ItemAiState.NotListed, (await GetAsync(owner, item.Id)).Ai!.State);
    }

    [Theory]
    [InlineData("indexed", "failed", "server_err", null, ItemAiState.Failed)]
    [InlineData("failed", null, null, "client_err", ItemAiState.NeedsAttention)]
    [InlineData("failed", null, null, "server_err", ItemAiState.Failed)]
    [InlineData("removed", null, null, null, ItemAiState.NotListed)]
    [InlineData("something_new", null, null, null, ItemAiState.NeedsAttention)]
    public void The_reason_code_says_whose_problem_it_is(string status, string? photoStatus, string? photoCode, string? textCode, ItemAiState expected)
    {
        Guid sent = Guid.NewGuid();
        ItemAiStatus row = new ItemAiStatus
        {
            SentEventId = sent,
            ReportedEventId = sent,
            Status = status,
            TextReasonCode = textCode,
            PhotosFailed = photoStatus == "failed" ? 1 : 0,
            Photos = photoStatus is null ? null
                : $$"""[{"media_id":"7a8b9c0d-1e2f-4a3b-8c4d-5e6f7a8b9c0d","status":"{{photoStatus}}","reason_code":"{{photoCode}}","reason":"photo host answered 404"}]""",
        };

        ItemAi ai = ItemAiView.Of(row, new Dictionary<Guid, string>())!;

        Assert.Equal(expected, ai.State);
        Assert.Null(ItemAiView.Of(new ItemAiStatus(), new Dictionary<Guid, string>()));
    }

    private Guid _lastReportId;

    private static object PhotoResult(Guid mediaId, string status, string? reasonCode, string? reason) => new
    {
        media_id = mediaId,
        variant_id = (Guid?)null,
        url = "https://cdn.example.com/media/x.jpg",
        status,
        embedded = status == "embedded",
        match_score = status == "embedded" ? 0.9 : 0.05,
        caption = status == "embedded" ? "A blue and pink patterned scarf." : "A cartoon chicken.",
        reason_code = reasonCode,
        reason,
    };

    /// <summary>A report in the shape of the AI team's contract (catalog.ingestion-report, schema 1).</summary>
    private string Report(Guid workspace, Guid productId, Guid causationId, DateTime occurredAt, object photo,
        Guid? reportId = null, string type = "ItemIndexed", string status = "indexed")
    {
        _lastReportId = reportId ?? Guid.NewGuid();
        return JsonSerializer.Serialize(new
        {
            event_id = _lastReportId,
            type,
            schema_version = 1,
            occurred_at = occurredAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            tenant_id = "owner",
            workspace_id = workspace,
            aggregate_type = "product",
            aggregate_id = productId,
            causation_id = causationId,
            payload = new
            {
                product_id = productId,
                version = 1,
                status,
                searchable = status == "indexed",
                text = new { status = "embedded", reason_code = (string?)null, reason = (string?)null },
                photos = new { status = "completed", total = 1, items = new[] { photo } },
                indexed_at = occurredAt,
            },
        });
    }

    private async Task<bool> HandleAsync(string json)
    {
        DbContextOptions<RelayDbContext> options = new DbContextOptionsBuilder<RelayDbContext>().UseNpgsql(_fx.Database.Relay).Options;
        await using RelayDbContext db = new RelayDbContext(options);
        IngestionReportHandler handler = new IngestionReportHandler(db, new CatalogMetrics(), TimeProvider.System, NullLogger<IngestionReportHandler>.Instance);
        return await handler.HandleAsync(json, CancellationToken.None);
    }

    private static async Task<ItemResponse> GetAsync(HttpClient client, Guid id)
        => await CatalogApiFixture.ReadAsync<ItemResponse>(await client.GetAsync($"/api/catalog/items/{id}"));

    private async Task<(Guid EventId, Guid MediaId, long Version)> LatestItemEventAsync(Guid workspace, Guid productId)
    {
        Guid eventId = await _fx.Database.ScalarAsync<Guid>(_fx.Database.Relay, workspace,
            "SELECT id FROM catalog.outbox WHERE aggregate_id = @id AND event_type IN ('ItemChanged', 'ItemRemoved') ORDER BY created_at DESC LIMIT 1",
            new Npgsql.NpgsqlParameter("id", productId));
        string payload = await _fx.Database.ScalarAsync<string>(_fx.Database.Relay, workspace,
            "SELECT payload::text FROM catalog.outbox WHERE id = @id", new Npgsql.NpgsqlParameter("id", eventId));
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;
        Guid mediaId = root.TryGetProperty("photos", out JsonElement photos) && photos.GetArrayLength() > 0 ? photos[0].GetProperty("media_id").GetGuid() : Guid.Empty;
        return (eventId, mediaId, root.GetProperty("version").GetInt64());
    }

    private static MultipartFormDataContent Photo()
    {
        ByteArrayContent file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { file, "file", "scarf.jpg" } };
    }
}
