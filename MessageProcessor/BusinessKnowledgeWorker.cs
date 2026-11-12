using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.BusinessSources;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace MessageProcessor;

/// <summary>
/// Polls for WhatsApp profile sources whose metadata is pending, fetches the WhatsApp
/// Business Profile from Meta, and enriches the BusinessSource. After a successful fetch,
/// publishes a snapshot so the AI sees the updated metadata. Failures are retried with
/// exponential backoff; sources that exceed MaxAttempts are marked Failed.
/// </summary>
public sealed class BusinessKnowledgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWhatsAppProfileClient _profileClient;
    private readonly ILogger<BusinessKnowledgeWorker> _logger;

    private const int BatchSize = 20;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public BusinessKnowledgeWorker(
        IServiceScopeFactory scopeFactory,
        IWhatsAppProfileClient profileClient,
        ILogger<BusinessKnowledgeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _profileClient = profileClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BusinessKnowledgeWorker started. BatchSize={BatchSize} PollIntervalMs={PollIntervalMs}",
            BatchSize, (int)PollInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BusinessKnowledgeWorker batch pass failed; retrying after poll interval.");
            }

            if (processed < BatchSize)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IBusinessSourceRepository sources = scope.ServiceProvider.GetRequiredService<IBusinessSourceRepository>();
        ISnapshotPublisher publisher = scope.ServiceProvider.GetRequiredService<ISnapshotPublisher>();
        IWhatsAppTokenProtector protector = scope.ServiceProvider.GetRequiredService<IWhatsAppTokenProtector>();

        // ── 1. Claim a batch inside a short transaction ──────────────────────
        List<(Guid Id, string WorkspaceId, string ExternalId, Guid ConnectionId)> claimed;
        {
            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            IReadOnlyList<BusinessSource> batch = await sources.ClaimBatchForMetadataAsync(
                BatchSize, LeaseDuration, Channel.WhatsApp, BusinessSourceKind.Profile, ct);

            if (batch.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            claimed = batch.Select(s => (s.Id, s.WorkspaceId, s.ExternalId, s.AuthorizationConnectionId)).ToList();
        }

        // ── 2. Process each claimed source outside any transaction ───────────
        int processed = 0;
        foreach ((Guid id, string workspaceId, string externalId, Guid connectionId) in claimed)
        {
            try
            {
                await ProcessOneAsync(db, publisher, protector, id, workspaceId, externalId, connectionId, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BusinessKnowledgeWorker source processing failed. SourceId={SourceId} WorkspaceId={WorkspaceId}",
                    id, workspaceId);
                // The lease will expire naturally; another pass will reclaim it.
            }
        }

        return processed;
    }

    private async Task ProcessOneAsync(
        ApplicationDbContext db,
        ISnapshotPublisher publisher,
        IWhatsAppTokenProtector protector,
        Guid sourceId,
        string workspaceId,
        string phoneNumberId,
        Guid connectionId,
        CancellationToken ct)
    {
        // Look up the connection and decrypt the token (no DB transaction needed).
        WhatsAppConnection? connection = await db.WhatsAppConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.DisabledAt == null, ct);

        string? accessToken = null;
        string? resolveError = null;

        if (connection is null || string.IsNullOrEmpty(connection.AccessTokenEncrypted))
        {
            resolveError = "Connection not found, disabled, or missing access token.";
        }
        else
        {
            try
            {
                accessToken = protector.Unprotect(connection.AccessTokenEncrypted);
            }
            catch (Exception ex)
            {
                resolveError = $"Token decryption failed: {ex.Message}";
                _logger.LogWarning(ex, "whatsapp.profile.token_decrypt_failed SourceId={SourceId}", sourceId);
            }
        }

        // Fetch profile from Meta (outside DB transaction).
        WhatsAppProfileData? profile = null;
        string? fetchError = resolveError;

        if (accessToken is not null)
        {
            profile = await _profileClient.GetProfileAsync(phoneNumberId, accessToken, ct);
            if (profile is null)
                fetchError = "Meta API returned an error; see WhatsAppProfileClient warning log.";
        }

        // ── Persist the result in a short transaction ────────────────────────
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

        // Find the source in the change tracker (it was loaded by ClaimBatchForMetadataAsync).
        BusinessSource? source = await db.BusinessSources.FindAsync([sourceId], ct);
        if (source is null)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        source.MetadataSyncAttemptCount += 1;
        source.MetadataSyncLockedUntil = null;

        if (profile is not null)
        {
            source.DisplayName = profile.VerifiedName;
            source.Handle = profile.DisplayPhoneNumber;
            source.Url = profile.Websites.Count > 0 ? profile.Websites[0] : null;
            source.Text = BuildText(profile);
            source.CapturedAt = DateTime.UtcNow;
            source.MetadataSyncStatus = MetadataSyncStatus.Completed;
            source.LastMetadataSyncSucceededAt = DateTime.UtcNow;
            source.LastMetadataSyncError = null;

            await db.SaveChangesAsync(ct);
            await publisher.PublishAsync(workspaceId, ct);

            _logger.LogInformation("whatsapp.profile.metadata_fetched SourceId={SourceId} WorkspaceId={WorkspaceId} PhoneNumberId={PhoneNumberId}",
                sourceId, workspaceId, phoneNumberId);
        }
        else
        {
            bool exhausted = source.MetadataSyncAttemptCount >= MaxAttempts;
            source.MetadataSyncStatus = exhausted ? MetadataSyncStatus.Failed : MetadataSyncStatus.RetryScheduled;
            source.LastMetadataSyncError = fetchError?.Length > 2000 ? fetchError[..2000] : fetchError;

            if (!exhausted)
            {
                // Exponential backoff: 2m, 4m, 8m, 16m, 32m — capped at 1h.
                int backoffMinutes = Math.Min(60, 2 * (int)Math.Pow(2, source.MetadataSyncAttemptCount - 1));
                source.NextMetadataSyncAttemptAt = DateTime.UtcNow.AddMinutes(backoffMinutes);
            }

            await db.SaveChangesAsync(ct);

            _logger.LogWarning("whatsapp.profile.metadata_fetch_failed SourceId={SourceId} AttemptCount={AttemptCount} Exhausted={Exhausted} Error={Error}",
                sourceId, source.MetadataSyncAttemptCount, exhausted, fetchError);
        }

        await tx.CommitAsync(ct);
    }

    private static string BuildText(WhatsAppProfileData profile)
    {
        // A flat JSON object so the AI can reference individual fields.
        Dictionary<string, object?> fields = new();
        if (!string.IsNullOrWhiteSpace(profile.About)) fields["about"] = profile.About;
        if (!string.IsNullOrWhiteSpace(profile.Description)) fields["description"] = profile.Description;
        if (!string.IsNullOrWhiteSpace(profile.Address)) fields["address"] = profile.Address;
        if (!string.IsNullOrWhiteSpace(profile.Email)) fields["email"] = profile.Email;
        if (!string.IsNullOrWhiteSpace(profile.Vertical)) fields["vertical"] = profile.Vertical;
        if (profile.Websites.Count > 0) fields["websites"] = profile.Websites;
        return JsonSerializer.Serialize(fields);
    }
}
