using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

public class WhatsAppConnectionService : IWhatsAppConnectionService
{
    private readonly IWhatsAppConnectionRepository _repository;
    private readonly IWhatsAppTokenProtector _protector;
    private readonly IConnectionSourceSyncService _sourceSyncService;
    private readonly IAppLogger<WhatsAppConnectionService> _logger;
    private readonly string _baseUrl;

    public WhatsAppConnectionService(
        IWhatsAppConnectionRepository repository,
        IWhatsAppTokenProtector protector,
        IConnectionSourceSyncService sourceSyncService,
        IAppLogger<WhatsAppConnectionService> logger,
        IOptions<WhatsAppSettings> settings)
    {
        _repository = repository;
        _protector = protector;
        _sourceSyncService = sourceSyncService;
        _logger = logger;
        _baseUrl = settings.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("WhatsApp:WebhookBaseUrl must be an absolute public API URL without credentials, query or fragment.");
        }
    }

    public async Task<WhatsAppConnectionSetupResponse> CreateAsync(string workspaceId, CreateWhatsAppConnectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        WhatsAppConnection? existing = await FindExistingAsync(workspaceId);
        if (existing is not null)
            return Resume(existing);
        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 128)
        {
            throw new ValidationException("Connection name must contain between 1 and 128 characters.");
        }

        string verifyToken = NewCredential();
        WhatsAppConnection connection = new()
        {
            WorkspaceId = workspaceId,
            Name = name,
            VerifyTokenEncrypted = _protector.Protect(verifyToken),
            WhatsAppBusinessAccountId = Normalize(request.WhatsAppBusinessAccountId, 64),
            PhoneNumberId = Normalize(request.PhoneNumberId, 64),
            DisplayPhoneNumber = Normalize(request.DisplayPhoneNumber, 32),
        };
        try
        {
            await _repository.AddAsync(connection);
        }
        catch (ConflictException)
        {
            // Another tab may have created the owner's connection while this request was running.
            existing = await FindExistingAsync(workspaceId);
            if (existing is null) throw;
            return Resume(existing);
        }
        return ToSetup(connection, verifyToken);
    }

    private async Task<WhatsAppConnection?> FindExistingAsync(string workspaceId) =>
        (await _repository.GetByOwnerAsync(workspaceId))
        .OrderBy(c => c.DisabledAt.HasValue)
        .ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).FirstOrDefault();

    private WhatsAppConnectionSetupResponse Resume(WhatsAppConnection connection)
    {
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("Your WhatsApp connection is disabled. Contact support to continue.");
        return ToSetup(connection, _protector.Unprotect(connection.VerifyTokenEncrypted));
    }

    public async Task<IReadOnlyList<WhatsAppConnectionResponse>> GetAllAsync(string workspaceId)
    {
        IReadOnlyList<WhatsAppConnection> connections = await _repository.GetByOwnerAsync(workspaceId);
        return connections.Select(ToResponse).ToList();
    }

    public async Task<WhatsAppConnectionResponse> GetByIdAsync(string workspaceId, Guid id) =>
        ToResponse(await GetOwnedAsync(workspaceId, id));

    public async Task<WhatsAppConnectionSetupResponse> GetSetupAsync(string workspaceId, Guid id)
    {
        WhatsAppConnection connection = await GetOwnedAsync(workspaceId, id);
        if (connection.DisabledAt.HasValue)
        {
            throw new ConflictException("This WhatsApp connection is disabled.");
        }

        return ToSetup(connection, _protector.Unprotect(connection.VerifyTokenEncrypted));
    }

    public async Task DisableAsync(string workspaceId, Guid id)
    {
        await GetOwnedAsync(workspaceId, id);
        await _repository.DisableAsync(id, workspaceId, DateTime.UtcNow);

        // Best-effort: deactivate sources so the AI snapshot no longer lists this connection.
        try { await _sourceSyncService.DeactivateConnectionSourcesAsync(workspaceId, id); }
        catch (Exception ex) { _logger.LogWarning(ex, "whatsapp.sources.deactivate_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}", workspaceId, id); }
    }

    public async Task ConfigureAccountAsync(string workspaceId, Guid id, WhatsAppAccountRequest request)
    {
        await GetOwnedAsync(workspaceId, id);
        if (!IsMetaId(request.WhatsAppBusinessAccountId) || !IsMetaId(request.PhoneNumberId))
            throw new ValidationException("Enter valid numeric Meta business account and phone number IDs (up to 64 digits).");
        await _repository.ConfigureAccountAsync(id, workspaceId, request.WhatsAppBusinessAccountId, request.PhoneNumberId);

        // Best-effort: publish the WhatsApp profile source so the AI knows this workspace is on WhatsApp.
        // A failure here does not roll back the account configuration; the reconciliation job will catch up.
        try
        {
            await _sourceSyncService.SyncWhatsAppProfileAsync(workspaceId, id, request.PhoneNumberId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "whatsapp.profile.source.sync_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}; connection is configured but snapshot was not published.",
                workspaceId, id);
        }
    }

    public async Task SaveAccessTokenAsync(string workspaceId, Guid id, WhatsAppAccessTokenRequest request)
    {
        WhatsAppConnection connection = await GetOwnedAsync(workspaceId, id);
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("This WhatsApp connection is disabled.");
        string token = request.AccessToken?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 8192 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ValidationException("Enter a valid access token of up to 8192 characters.");
        await _repository.SaveAccessTokenAsync(id, workspaceId, _protector.Protect(token));
    }

    // Manual setup: the customer's own Meta app signs this connection's webhooks with it.
    public async Task SaveAppSecretAsync(string workspaceId, Guid id, AppSecretRequest request)
    {
        WhatsAppConnection connection = await GetOwnedAsync(workspaceId, id);
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("This WhatsApp connection is disabled.");
        string secret = request.AppSecret?.Trim() ?? string.Empty;
        if (!WebhookSignature.IsAppSecret(secret))
            throw new ValidationException("Enter the App Secret from your Meta app's App settings > Basic: 32 letters and numbers.");
        await _repository.SaveAppSecretAsync(id, workspaceId, _protector.Protect(secret));
    }

    private static bool IsMetaId(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => c is >= '0' and <= '9');

    public async Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(verifyToken))
        {
            return false;
        }

        WhatsAppConnection? connection = await _repository.GetActiveByIdentifierAsync(identifier);
        if (connection is null)
        {
            return false;
        }

        string expected;
        try
        {
            expected = _protector.Unprotect(connection.VerifyTokenEncrypted);
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(verifyToken)))
        {
            return false;
        }

        return await _repository.MarkWebhookVerifiedAsync(connection.Id, DateTime.UtcNow);
    }

    private static string NewCredential() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string? Normalize(string? value, int maxLength)
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength)
        {
            throw new ValidationException($"Connection field exceeds its maximum length of {maxLength}.");
        }

        return normalized;
    }

    private async Task<WhatsAppConnection> GetOwnedAsync(string workspaceId, Guid id) =>
        await _repository.GetByOwnerAndIdAsync(workspaceId, id)
        ?? throw new NotFoundException("WhatsApp connection not found.");

    private WhatsAppConnectionSetupResponse ToSetup(WhatsAppConnection connection, string token) => new()
    {
        Connection = ToResponse(connection),
        WebhookUrl = $"{_baseUrl}/api/webhooks/whatsapp?ref={connection.WebhookIdentifier}",
        VerifyToken = token,
    };

    private static WhatsAppConnectionResponse ToResponse(WhatsAppConnection connection) => new()
    {
        HasAccessToken = !string.IsNullOrEmpty(connection.AccessTokenEncrypted),
        HasAppSecret = !string.IsNullOrEmpty(connection.AppSecretEncrypted),
        Id = connection.Id,
        Name = connection.Name,
        SetupStatus = connection.SetupStatus,
        WhatsAppBusinessAccountId = connection.WhatsAppBusinessAccountId,
        PhoneNumberId = connection.PhoneNumberId,
        DisplayPhoneNumber = connection.DisplayPhoneNumber,
        WebhookVerifiedAt = connection.WebhookVerifiedAt,
        FirstMessageReceivedAt = connection.FirstMessageReceivedAt,
        DisabledAt = connection.DisabledAt,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
    };
}
