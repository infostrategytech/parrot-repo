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
/// Polls for Profile sources (WhatsApp, Instagram, Facebook) whose metadata is pending,
/// fetches the business profile from the respective Meta API, and enriches the BusinessSource.
/// After a successful fetch, publishes a snapshot so the AI sees the updated metadata.
/// Failures are retried with exponential backoff; sources exceeding MaxAttempts are marked Failed.
/// </summary>
public sealed class BusinessKnowledgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWhatsAppProfileClient _whatsAppProfileClient;
    private readonly IInstagramProfileClient _instagramProfileClient;
    private readonly IFacebookPageClient _facebookPageClient;
    private readonly ILogger<BusinessKnowledgeWorker> _logger;

    private const int BatchSize = 20;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public BusinessKnowledgeWorker(
        IServiceScopeFactory scopeFactory,
        IWhatsAppProfileClient whatsAppProfileClient,
        IInstagramProfileClient instagramProfileClient,
        IFacebookPageClient facebookPageClient,
        ILogger<BusinessKnowledgeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _whatsAppProfileClient = whatsAppProfileClient;
        _instagramProfileClient = instagramProfileClient;
        _facebookPageClient = facebookPageClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BusinessKnowledgeWorker started. Channels=WhatsApp,Instagram,Facebook Kind=Profile BatchSize={BatchSize} PollIntervalMs={PollIntervalMs}",
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
        IWhatsAppTokenProtector whatsAppProtector = scope.ServiceProvider.GetRequiredService<IWhatsAppTokenProtector>();
        IInstagramCredentialProtector instagramProtector = scope.ServiceProvider.GetRequiredService<IInstagramCredentialProtector>();
        IFacebookCredentialProtector facebookProtector = scope.ServiceProvider.GetRequiredService<IFacebookCredentialProtector>();

        // ── 1. Claim a batch inside a short transaction (all channels, Profile kind) ─
        List<(Guid Id, string WorkspaceId, string ExternalId, Guid ConnectionId, Channel Channel)> claimed;
        {
            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            IReadOnlyList<BusinessSource> batch = await sources.ClaimBatchForMetadataAsync(
                BatchSize, LeaseDuration, channel: null, kind: BusinessSourceKind.Profile, ct);

            if (batch.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            claimed = batch
                .Select(s => (s.Id, s.WorkspaceId, s.ExternalId, s.AuthorizationConnectionId, s.Channel))
                .ToList();
        }

        // ── 2. Process each claimed source outside any transaction ───────────
        int processed = 0;
        foreach ((Guid id, string workspaceId, string externalId, Guid connectionId, Channel channel) in claimed)
        {
            try
            {
                await ProcessOneAsync(db, publisher, whatsAppProtector, instagramProtector, facebookProtector,
                    id, workspaceId, externalId, connectionId, channel, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "BusinessKnowledgeWorker source processing failed. SourceId={SourceId} WorkspaceId={WorkspaceId} Channel={Channel}",
                    id, workspaceId, channel);
                // The lease will expire naturally; another pass will reclaim it.
            }
        }

        return processed;
    }

    private async Task ProcessOneAsync(
        ApplicationDbContext db,
        ISnapshotPublisher publisher,
        IWhatsAppTokenProtector whatsAppProtector,
        IInstagramCredentialProtector instagramProtector,
        IFacebookCredentialProtector facebookProtector,
        Guid sourceId,
        string workspaceId,
        string externalId,
        Guid connectionId,
        Channel channel,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "business_knowledge.process_one.start SourceId={SourceId} Channel={Channel} ExternalId={ExternalId} ConnectionId={ConnectionId}",
            sourceId, channel, externalId, connectionId);

        // Resolve access token and fetch profile outside any DB transaction.
        (string? accessToken, string? resolveError) = channel switch
        {
            Channel.WhatsApp => await ResolveWhatsAppTokenAsync(db, connectionId, sourceId, whatsAppProtector, ct),
            Channel.Instagram => await ResolveInstagramTokenAsync(db, connectionId, sourceId, instagramProtector, ct),
            Channel.Facebook => await ResolveFacebookTokenAsync(db, connectionId, sourceId, facebookProtector, ct),
            _ => (null, $"Unsupported channel {channel}."),
        };

        if (resolveError is not null)
            _logger.LogWarning(
                "business_knowledge.token_resolve_failed SourceId={SourceId} Channel={Channel} Reason={Reason}",
                sourceId, channel, resolveError);

        // Fetch metadata from the channel API (outside DB transaction).
        (bool success, string? fetchError, Action<BusinessSource>? apply) = await FetchMetadataAsync(
            channel, externalId, accessToken, sourceId, ct);

        // ── Persist the result in a short transaction ────────────────────────
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

        BusinessSource? source = await db.BusinessSources.FindAsync([sourceId], ct);
        if (source is null)
        {
            await tx.RollbackAsync(ct);
            _logger.LogWarning("business_knowledge.source_gone SourceId={SourceId}", sourceId);
            return;
        }

        source.MetadataSyncAttemptCount += 1;
        source.MetadataSyncLockedUntil = null;

        if (success && apply is not null)
        {
            apply(source);
            source.CapturedAt = DateTime.UtcNow;
            source.MetadataSyncStatus = MetadataSyncStatus.Completed;
            source.LastMetadataSyncSucceededAt = DateTime.UtcNow;
            source.LastMetadataSyncError = null;

            await db.SaveChangesAsync(ct);
            await publisher.PublishAsync(workspaceId, ct);

            _logger.LogInformation(
                "business_knowledge.metadata_fetched SourceId={SourceId} Channel={Channel} WorkspaceId={WorkspaceId} ExternalId={ExternalId}",
                sourceId, channel, workspaceId, externalId);
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

            _logger.LogWarning(
                "business_knowledge.metadata_fetch_failed SourceId={SourceId} Channel={Channel} AttemptCount={AttemptCount} Exhausted={Exhausted} Error={Error}",
                sourceId, channel, source.MetadataSyncAttemptCount, exhausted, fetchError);
        }

        await tx.CommitAsync(ct);
    }

    // ── Token resolution ─────────────────────────────────────────────────────

    private async Task<(string? Token, string? Error)> ResolveWhatsAppTokenAsync(
        ApplicationDbContext db, Guid connectionId, Guid sourceId, IWhatsAppTokenProtector protector, CancellationToken ct)
    {
        WhatsAppConnection? connection = await db.WhatsAppConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.DisabledAt == null, ct);

        if (connection is null)
        {
            _logger.LogWarning("business_knowledge.whatsapp_connection_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "WhatsApp connection not found or disabled.");
        }

        if (string.IsNullOrEmpty(connection.AccessTokenEncrypted))
        {
            _logger.LogWarning("business_knowledge.whatsapp_token_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "WhatsApp connection has no access token.");
        }

        try
        {
            return (protector.Unprotect(connection.AccessTokenEncrypted), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "business_knowledge.whatsapp_token_decrypt_failed SourceId={SourceId}", sourceId);
            return (null, $"WhatsApp token decryption failed: {ex.Message}");
        }
    }

    private async Task<(string? Token, string? Error)> ResolveInstagramTokenAsync(
        ApplicationDbContext db, Guid connectionId, Guid sourceId, IInstagramCredentialProtector protector, CancellationToken ct)
    {
        InstagramConnection? connection = await db.InstagramConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.DisabledAt == null, ct);

        if (connection is null)
        {
            _logger.LogWarning("business_knowledge.instagram_connection_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "Instagram connection not found or disabled.");
        }

        if (string.IsNullOrEmpty(connection.AccessTokenEncrypted))
        {
            _logger.LogWarning("business_knowledge.instagram_token_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "Instagram connection has no access token.");
        }

        try
        {
            return (protector.Unprotect(connection.AccessTokenEncrypted), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "business_knowledge.instagram_token_decrypt_failed SourceId={SourceId}", sourceId);
            return (null, $"Instagram token decryption failed: {ex.Message}");
        }
    }

    private async Task<(string? Token, string? Error)> ResolveFacebookTokenAsync(
        ApplicationDbContext db, Guid connectionId, Guid sourceId, IFacebookCredentialProtector protector, CancellationToken ct)
    {
        FacebookConnection? connection = await db.FacebookConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.DisabledAt == null, ct);

        if (connection is null)
        {
            _logger.LogWarning("business_knowledge.facebook_connection_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "Facebook connection not found or disabled.");
        }

        if (string.IsNullOrEmpty(connection.AccessTokenEncrypted))
        {
            _logger.LogWarning("business_knowledge.facebook_token_missing SourceId={SourceId} ConnectionId={ConnectionId}",
                sourceId, connectionId);
            return (null, "Facebook connection has no access token.");
        }

        try
        {
            return (protector.Unprotect(connection.AccessTokenEncrypted), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "business_knowledge.facebook_token_decrypt_failed SourceId={SourceId}", sourceId);
            return (null, $"Facebook token decryption failed: {ex.Message}");
        }
    }

    // ── Metadata fetch ───────────────────────────────────────────────────────

    private async Task<(bool Success, string? Error, Action<BusinessSource>? Apply)> FetchMetadataAsync(
        Channel channel, string externalId, string? accessToken, Guid sourceId, CancellationToken ct)
    {
        if (accessToken is null)
            return (false, "Access token could not be resolved.", null);

        switch (channel)
        {
            case Channel.WhatsApp:
            {
                WhatsAppProfileData? profile = await _whatsAppProfileClient.GetProfileAsync(externalId, accessToken, ct);
                if (profile is null)
                    return (false, "Meta API returned an error; see WhatsAppProfileClient warning log.", null);

                return (true, null, source =>
                {
                    source.DisplayName = profile.VerifiedName;
                    source.Handle = profile.DisplayPhoneNumber;
                    source.Url = profile.Websites.Count > 0 ? profile.Websites[0] : null;
                    source.Text = BuildWhatsAppText(profile);
                });
            }

            case Channel.Instagram:
            {
                InstagramProfileData? profile = await _instagramProfileClient.GetProfileAsync(externalId, accessToken, ct);
                if (profile is null)
                    return (false, "Instagram Graph API returned an error; see InstagramProfileClient warning log.", null);

                return (true, null, source =>
                {
                    source.DisplayName = profile.Name;
                    source.Url = profile.Website;
                    source.Text = BuildInstagramText(profile);
                });
            }

            case Channel.Facebook:
            {
                FacebookPageData? page = await _facebookPageClient.GetPageAsync(externalId, accessToken, ct);
                if (page is null)
                    return (false, "Facebook Graph API returned an error; see FacebookPageClient warning log.", null);

                return (true, null, source =>
                {
                    source.DisplayName = page.Name;
                    source.Handle = externalId;
                    source.Url = page.Website;
                    source.Text = BuildFacebookText(page);
                });
            }

            default:
                return (false, $"Unsupported channel {channel}.", null);
        }
    }

    // ── Text builders ────────────────────────────────────────────────────────

    private static string BuildWhatsAppText(WhatsAppProfileData profile)
    {
        Dictionary<string, object?> fields = new();
        if (!string.IsNullOrWhiteSpace(profile.About)) fields["about"] = profile.About;
        if (!string.IsNullOrWhiteSpace(profile.Description)) fields["description"] = profile.Description;
        if (!string.IsNullOrWhiteSpace(profile.Address)) fields["address"] = profile.Address;
        if (!string.IsNullOrWhiteSpace(profile.Email)) fields["email"] = profile.Email;
        if (!string.IsNullOrWhiteSpace(profile.Vertical)) fields["vertical"] = profile.Vertical;
        if (profile.Websites.Count > 0) fields["websites"] = profile.Websites;
        return JsonSerializer.Serialize(fields);
    }

    private static string BuildInstagramText(InstagramProfileData profile)
    {
        Dictionary<string, object?> fields = new();
        if (!string.IsNullOrWhiteSpace(profile.Biography)) fields["biography"] = profile.Biography;
        if (profile.FollowerCount.HasValue) fields["followers_count"] = profile.FollowerCount.Value;
        if (!string.IsNullOrWhiteSpace(profile.ProfilePictureUrl)) fields["profile_picture_url"] = profile.ProfilePictureUrl;
        return JsonSerializer.Serialize(fields);
    }

    private static string BuildFacebookText(FacebookPageData page)
    {
        Dictionary<string, object?> fields = new();
        if (!string.IsNullOrWhiteSpace(page.About)) fields["about"] = page.About;
        if (!string.IsNullOrWhiteSpace(page.Description)) fields["description"] = page.Description;
        if (!string.IsNullOrWhiteSpace(page.Category)) fields["category"] = page.Category;
        if (page.FanCount.HasValue) fields["fan_count"] = page.FanCount.Value;
        return JsonSerializer.Serialize(fields);
    }
}
