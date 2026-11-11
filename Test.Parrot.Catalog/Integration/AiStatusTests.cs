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
/// The AI team reports on catalog.ingestion_report how far it got with each item message.
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

        object Form(int amount) => new
        {
            name = "Adire scarf",
            kind = "product",
            pricing = new { mode = "single", amount },
            availability = new { mode = "in_stock" },
            status = "published",
            photos = new[] { photo.Key },
        };

        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Body(Form(5000))));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);

        (Guid firstSave, Guid mediaId) = await LatestItemEventAsync(workspace, item.Id);

        // The AI rejects the photo: the business is asked to look at it, with the AI's reason.
        Assert.True(await HandleAsync(Report(workspace, item.Id, firstSave, mediaId, "rejected", "The photo shows a cartoon chicken, not an adire scarf.")));
        ItemAi ai = (await GetAsync(owner, item.Id)).Ai!;
        Assert.Equal(ItemAiState.NeedsAttention, ai.State);
        ItemAiIssue issue = Assert.Single(ai.Issues);
        Assert.Equal(photo.Key, issue.PhotoKey);
        Assert.Equal("rejected", issue.Status);
        Assert.Equal("The photo shows a cartoon chicken, not an adire scarf.", issue.Reason);

        // The same report delivered twice is recorded once.
        string again = Report(workspace, item.Id, firstSave, mediaId, "rejected", "again", reportId: _lastReportId);
        Assert.False(await HandleAsync(again));

        // A new save: updating again, and a late report about the first save doesn't change that.
        await owner.PutAsync($"/api/catalog/items/{item.Id}", CatalogApiFixture.Body(Form(6000)));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);
        Assert.False(await HandleAsync(Report(workspace, item.Id, firstSave, mediaId, "embedded", null)));
        Assert.Equal(ItemAiState.Updating, (await GetAsync(owner, item.Id)).Ai!.State);

        // A report naming another workspace is ignored.
        (Guid secondSave, _) = await LatestItemEventAsync(workspace, item.Id);
        Assert.False(await HandleAsync(Report(Guid.NewGuid(), item.Id, secondSave, mediaId, "embedded", null)));

        // The report about the latest save: ready.
        Assert.True(await HandleAsync(Report(workspace, item.Id, secondSave, mediaId, "embedded", null)));
        ai = (await GetAsync(owner, item.Id)).Ai!;
        Assert.Equal(ItemAiState.Ready, ai.State);
        Assert.Empty(ai.Issues);
        Assert.NotNull(ai.ReportedAt);
    }

    [Fact]
    public void A_photo_the_ai_could_not_read_is_its_problem_not_the_business()
    {
        Guid sent = Guid.NewGuid();
        ItemAiStatus row = new ItemAiStatus
        {
            SentEventId = sent,
            ReportedEventId = sent,
            Status = "indexed",
            PhotosFailed = 1,
            Photos = """[{"media_id":"7a8b9c0d-1e2f-4a3b-8c4d-5e6f7a8b9c0d","status":"failed","reason":"photo host answered 404"}]""",
        };

        ItemAi ai = ItemAiView.Of(row, new Dictionary<Guid, string>())!;

        Assert.Equal(ItemAiState.Failed, ai.State);
        Assert.Equal("photo host answered 404", Assert.Single(ai.Issues).Reason);
        Assert.Null(ItemAiView.Of(new ItemAiStatus(), new Dictionary<Guid, string>()));
    }

    private Guid _lastReportId;

    private string Report(Guid workspace, Guid productId, Guid sourceEventId, Guid mediaId, string photoStatus, string? reason, Guid? reportId = null)
    {
        _lastReportId = reportId ?? Guid.NewGuid();
        return JsonSerializer.Serialize(new
        {
            event_id = _lastReportId,
            type = "ItemIndexed",
            schema_version = 1,
            occurred_at = DateTime.UtcNow,
            tenant_id = "owner",
            workspace_id = workspace,
            source_event_id = sourceEventId,
            aggregate_type = "product",
            aggregate_id = productId,
            payload = new
            {
                product_id = productId,
                status = "indexed",
                text = new { status = "embedded" },
                photos = new
                {
                    status = "completed",
                    items = new[] { new { media_id = mediaId, status = photoStatus, reason } },
                },
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

    private async Task<(Guid EventId, Guid MediaId)> LatestItemEventAsync(Guid workspace, Guid productId)
    {
        Guid eventId = await _fx.Database.ScalarAsync<Guid>(_fx.Database.Relay, workspace,
            "SELECT id FROM catalog.outbox WHERE aggregate_id = @id AND event_type = 'ItemChanged' ORDER BY created_at DESC LIMIT 1",
            new Npgsql.NpgsqlParameter("id", productId));
        string payload = await _fx.Database.ScalarAsync<string>(_fx.Database.Relay, workspace,
            "SELECT payload::text FROM catalog.outbox WHERE id = @id", new Npgsql.NpgsqlParameter("id", eventId));
        using JsonDocument doc = JsonDocument.Parse(payload);
        return (eventId, doc.RootElement.GetProperty("photos")[0].GetProperty("media_id").GetGuid());
    }

    private static MultipartFormDataContent Photo()
    {
        ByteArrayContent file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { file, "file", "scarf.jpg" } };
    }
}
