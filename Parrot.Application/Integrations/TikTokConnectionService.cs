using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

// Setup orchestration for TikTok: the app, the webhook it is registered with, and the owner's consent.
public sealed class TikTokConnectionService(
    ITikTokConnectionRepository repository,
    ITikTokCredentialProtector protector,
    ITikTokApiClient tikTok,
    IOptions<TikTokSettings> options) : ITikTokConnectionService
{
    // What the account owner must grant for Parrot to read and answer their messages.
    public static readonly IReadOnlyList<string> Scopes =
        ["user.info.basic", "user.info.username", "user.info.profile", "message.list.read", "message.list.send", "message.list.manage"];

    // How long the owner has to approve on TikTok's consent screen.
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(15);

    public async Task<TikTokSetupResponse> GetSetupAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        string baseUrl = RequireBaseUrl();

        TikTokConnection? connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken);
        return new TikTokSetupResponse(connection?.Id, RedirectUri(baseUrl), connection?.AppId,
            connection?.TikTokOpenId is not null, connection?.DisplayName,
            connection is { TikTokOpenId: not null, RefreshTokenExpiresAt: { } refreshExpires } && refreshExpires <= DateTime.UtcNow);
    }

    public async Task SaveAppAsync(string ownerUserId, TikTokAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        string baseUrl = RequireBaseUrl();

        string appId = request.AppId?.Trim() ?? string.Empty;
        string appSecret = request.AppSecret?.Trim() ?? string.Empty;
        if (!IsCredential(appId))
            throw new ValidationException("Enter the App ID from your TikTok app's Basic Information.");
        if (!IsCredential(appSecret))
            throw new ValidationException("Enter the App Secret from your TikTok app's Basic Information.");
        if (await repository.GetByOwnerAsync(ownerUserId, cancellationToken) is not null)
            throw new ConflictException("TikTok is already set up. Disconnect it to use a different app.");

        TikTokConnection connection = new()
        {
            OwnerUserId = ownerUserId,
            AppId = appId,
            AppSecretEncrypted = protector.Protect(appSecret),
        };

        // Registered before saving: TikTok checks the App ID and secret, so a mistyped pair leaves nothing behind.
        await CallTikTokAsync(() => tikTok.RegisterWebhookAsync(appId, appSecret,
                $"{baseUrl}/api/webhooks/tiktok/{connection.WebhookIdentifier}", cancellationToken),
            "TikTok did not accept this App ID and App Secret", cancellationToken);
        await repository.AddAsync(connection, cancellationToken);
    }

    public async Task<TikTokAuthorizationResponse> GetAuthorizationAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        string baseUrl = RequireBaseUrl();

        TikTokConnection connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken)
            ?? throw new NotFoundException("Save your TikTok app's App ID and App Secret first.");

        // The connection id is fixed length, so the owner id after it needs no escaping.
        string state = protector.ProtectState($"{connection.Id:N}{ownerUserId}", AuthorizationLifetime);
        string url = TikTokSettings.RequireSecureUrl(options.Value.AuthorizeUrl, nameof(TikTokSettings.AuthorizeUrl))
            + $"?client_key={Uri.EscapeDataString(connection.AppId)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(string.Join(',', Scopes))}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri(baseUrl))}"
            + $"&state={Uri.EscapeDataString(state)}";
        return new TikTokAuthorizationResponse(url);
    }

    public async Task CompleteAuthorizationAsync(string? state, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
            throw new ValidationException("TikTok did not return an authorization. Start again.");
        string baseUrl = RequireBaseUrl();

        (Guid connectionId, string ownerUserId) = OpenState(state);
        TikTokConnection? connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken);
        if (connection is null || connection.Id != connectionId)
            throw new ConflictException("This TikTok setup was disconnected. Start again.");

        string appSecret = protector.Unprotect(connection.AppSecretEncrypted);
        TikTokTokens tokens = await CallTikTokAsync(() => tikTok.ExchangeCodeAsync(connection.AppId, appSecret, code,
            RedirectUri(baseUrl), cancellationToken), "TikTok did not complete the authorization", cancellationToken);

        string[] missing = Scopes.Except(tokens.Scopes, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new ValidationException($"Allow every permission Parrot asks for on TikTok. Missing: {string.Join(", ", missing)}.");

        // Webhooks and conversations are tied to the first account, so another one needs a new connection.
        if (connection.TikTokOpenId is { } authorized && !string.Equals(authorized, tokens.OpenId, StringComparison.Ordinal))
            throw new ConflictException("This TikTok app is already connected to a different account. Disconnect TikTok to use another account.");

        TikTokAccount account = await CallTikTokAsync(() => tikTok.GetAccountAsync(tokens.OpenId, tokens.AccessToken,
            cancellationToken), "TikTok did not return the account", cancellationToken);
        string displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName.Trim();

        DateTime now = DateTime.UtcNow;
        await repository.SaveAccountAsync(connection.Id, ownerUserId, tokens.OpenId, displayName,
            protector.Protect(tokens.AccessToken), now + tokens.AccessTokenLifetime,
            protector.Protect(tokens.RefreshToken), now + tokens.RefreshTokenLifetime, cancellationToken);
    }

    public async Task DisableAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        TikTokConnection connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken)
            ?? throw new NotFoundException("There is no TikTok connection to disconnect.");
        await repository.DisableAsync(connection.Id, ownerUserId, DateTime.UtcNow, cancellationToken);
    }

    private (Guid ConnectionId, string OwnerUserId) OpenState(string state)
    {
        string value;
        try
        {
            value = protector.UnprotectState(state);
        }
        catch (CryptographicException)
        {
            throw new ValidationException("This TikTok authorization has expired. Start again.");
        }

        return value.Length > 32 && Guid.TryParseExact(value[..32], "N", out Guid connectionId)
            ? (connectionId, value[32..])
            : throw new ValidationException("This TikTok authorization has expired. Start again.");
    }

    // TikTok's refusals are the customer's to fix; not reaching TikTok is worth a retry.
    private static async Task CallTikTokAsync(Func<Task> call, string refusal, CancellationToken cancellationToken) =>
        await CallTikTokAsync(async () => { await call(); return true; }, refusal, cancellationToken);

    private static async Task<T> CallTikTokAsync<T>(Func<Task<T>> call, string refusal, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (TikTokApiException ex)
        {
            throw new ValidationException($"{refusal}: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ServiceUnavailableException("TikTok could not be reached. Try again in a moment.", ex);
        }
    }

    // App IDs and secrets are single tokens: no spaces or control characters.
    private static bool IsCredential(string value) =>
        value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static string RedirectUri(string baseUrl) => $"{baseUrl}/api/tiktok/setup/callback";

    // TikTok calls back over HTTPS only, so the base URL must be public HTTPS.
    private string RequireBaseUrl()
    {
        string baseUrl = options.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("TikTok:WebhookBaseUrl must be a public HTTPS URL without query, fragment or credentials.");
        return baseUrl;
    }
}
