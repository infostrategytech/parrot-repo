using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

public class WhatsAppConnectionService : IWhatsAppConnectionService
{
    private readonly IWhatsAppConnectionRepository _repository;
    private readonly IWhatsAppTokenProtector _protector;
    private readonly string _baseUrl;

    public WhatsAppConnectionService(
        IWhatsAppConnectionRepository repository,
        IWhatsAppTokenProtector protector,
        IOptions<WhatsAppSettings> settings)
    {
        _repository = repository;
        _protector = protector;
        _baseUrl = settings.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("WhatsApp:WebhookBaseUrl must be an absolute public API URL without credentials, query or fragment.");
        }
    }

    public async Task<WhatsAppConnectionSetupResponse> CreateAsync(string ownerUserId, CreateWhatsAppConnectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        WhatsAppConnection? existing = await FindExistingAsync(ownerUserId);
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
            OwnerUserId = ownerUserId,
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
            existing = await FindExistingAsync(ownerUserId);
            if (existing is null) throw;
            return Resume(existing);
        }
        return ToSetup(connection, verifyToken);
    }

    private async Task<WhatsAppConnection?> FindExistingAsync(string ownerUserId) =>
        (await _repository.GetByOwnerAsync(ownerUserId))
        .OrderBy(c => c.DisabledAt.HasValue)
        .ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).FirstOrDefault();

    private WhatsAppConnectionSetupResponse Resume(WhatsAppConnection connection)
    {
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("Your WhatsApp connection is disabled. Contact support to continue.");
        return ToSetup(connection, _protector.Unprotect(connection.VerifyTokenEncrypted));
    }

    public async Task<IReadOnlyList<WhatsAppConnectionResponse>> GetAllAsync(string ownerUserId)
    {
        IReadOnlyList<WhatsAppConnection> connections = await _repository.GetByOwnerAsync(ownerUserId);
        return connections.Select(ToResponse).ToList();
    }

    public async Task<WhatsAppConnectionResponse> GetByIdAsync(string ownerUserId, Guid id) =>
        ToResponse(await GetOwnedAsync(ownerUserId, id));

    public async Task<WhatsAppConnectionSetupResponse> GetSetupAsync(string ownerUserId, Guid id)
    {
        WhatsAppConnection connection = await GetOwnedAsync(ownerUserId, id);
        if (connection.DisabledAt.HasValue)
        {
            throw new ConflictException("This WhatsApp connection is disabled.");
        }

        return ToSetup(connection, _protector.Unprotect(connection.VerifyTokenEncrypted));
    }

    public async Task DisableAsync(string ownerUserId, Guid id)
    {
        await GetOwnedAsync(ownerUserId, id);
        await _repository.DisableAsync(id, ownerUserId, DateTime.UtcNow);
    }

    public async Task ConfigureAccountAsync(string ownerUserId, Guid id, WhatsAppAccountRequest request)
    {
        await GetOwnedAsync(ownerUserId, id);
        if (!IsMetaId(request.WhatsAppBusinessAccountId) || !IsMetaId(request.PhoneNumberId))
            throw new ValidationException("Enter valid numeric Meta business account and phone number IDs (up to 64 digits).");
        await _repository.ConfigureAccountAsync(id, ownerUserId, request.WhatsAppBusinessAccountId, request.PhoneNumberId);
    }

    public async Task SaveAccessTokenAsync(string ownerUserId, Guid id, WhatsAppAccessTokenRequest request)
    {
        WhatsAppConnection connection = await GetOwnedAsync(ownerUserId, id);
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("This WhatsApp connection is disabled.");
        string token = request.AccessToken?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 8192 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ValidationException("Enter a valid access token of up to 8192 characters.");
        await _repository.SaveAccessTokenAsync(id, ownerUserId, _protector.Protect(token));
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

    private async Task<WhatsAppConnection> GetOwnedAsync(string ownerUserId, Guid id) =>
        await _repository.GetByOwnerAndIdAsync(ownerUserId, id)
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
