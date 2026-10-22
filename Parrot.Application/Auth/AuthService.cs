using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Email;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Application.Utils;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Auth;

public class AuthService : IAuthService
{
    // Claim names the catalog service validates (see Parrot.Catalog.Application.Security.CatalogClaims).
    private const string CatalogTenantClaim = "tenant_id";
    private const string ScopeClaim = "scope";

    private const string InvalidCodeMessage = AuthMessages.InvalidCode;
    private const string TooManyCodesMessage = AuthMessages.TooManyCodes;

    // Hashed against when an address is unknown, so a miss costs the same as a
    // wrong password and the response time does not say which it was.
    private static readonly ApplicationUser TimingPlaceholder = new() { UserName = "-" };

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPasswordHasher<ApplicationUser> _passwordHasher;
    private readonly IJwtHelper _jwtHelper;
    private readonly IWorkspaceService _workspaces;
    private readonly IEmailService _emailService;
    private readonly IFeatureFlagService _featureFlags;
    private readonly EmailSettings _emailSettings;
    private readonly AuthSettings _authSettings;
    private readonly IAppLogger<AuthService> _logger;
    private readonly IWebHostEnvironment _env;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IJwtHelper jwtHelper,
        IWorkspaceService workspaces,
        IEmailService emailService,
        IFeatureFlagService featureFlags,
        IOptions<EmailSettings> emailSettings,
        IOptions<AuthSettings> authSettings,
        IAppLogger<AuthService> logger,
        IWebHostEnvironment env)
    {
        _userManager = userManager;
        _passwordHasher = passwordHasher;
        _jwtHelper = jwtHelper;
        _workspaces = workspaces;
        _emailService = emailService;
        _featureFlags = featureFlags;
        _emailSettings = emailSettings.Value;
        _authSettings = authSettings.Value;
        _logger = logger;
        _env = env;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        (ApplicationUser? user, string identifierKind) = await FindByIdentifierAsync(request.Identifier);

        if (user is null)
        {
            _passwordHasher.HashPassword(TimingPlaceholder, request.Password);
            _logger.LogWarning("Login attempt for unrecognised {IdentifierKind}", identifierKind);
            throw new UnauthorizedException(AuthMessages.InvalidCredentials);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Login blocked — account is locked out. Identifier: {Identifier}", request.Identifier);
            }

            throw new UnauthorizedException(AuthMessages.AccountLocked);
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            await _userManager.AccessFailedAsync(user);
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Failed login attempt for {Identifier}", request.Identifier);
            }

            throw new UnauthorizedException(AuthMessages.InvalidCredentials);
        }

        if (!user.EmailConfirmed)
        {
            if (await RequireEmailVerificationAsync())
            {
                // The password is right, so this is the account's owner coming back
                // to a sign-up they never finished. Send a fresh code and let the
                // client take them straight to the verification screen.
                using (_logger.BeginEntityScope("User", user.Id))
                {
                    _logger.LogWarning("Login attempt before email verification; sending a new code. Email: {Email}", user.Email);
                }

                await TrySendVerificationEmailAsync(user);
                throw new EmailNotVerifiedException();
            }

            // Verification is switched off: let them in, but the address stays
            // unconfirmed. EmailConfirmed means the address was proved (a code,
            // Google or Facebook, an invite link), which an invite relies on.
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("User logged in successfully. Email: {Email}", user.Email);
        }

        return await BuildAuthResponseAsync(user);
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        bool requireVerification = await RequireEmailVerificationAsync();

        ApplicationUser? existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
        {
            // An account that never received its verification code would otherwise be
            // stuck for good: signing up again conflicts, and signing in is refused
            // until the address is confirmed. Treat the retry as a resend instead.
            // The password is deliberately left alone, so a pending sign-up cannot be
            // taken over by someone else registering the same address. With
            // verification switched off the account can simply sign in.
            if (!existingUser.EmailConfirmed && requireVerification)
            {
                _logger.LogWarning("Registration retried for an unverified account; resending verification to {Email}", request.Email);
                await TrySendVerificationEmailAsync(existingUser);
                return null;
            }

            _logger.LogWarning("Registration attempt with already-registered email {Email}", request.Email);
            throw new ConflictException(AuthMessages.AccountExists);
        }

        ApplicationUser user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            // Not proved yet, even with verification switched off: signing in does
            // not need it then, but an invite link can still prove it later.
            EmailConfirmed = false,
        };

        IdentityResult result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            // Almost always the password policy; its own wording is what the
            // person needs to see to fix it.
            _logger.LogWarning("Identity user creation failed for {Email}: {Errors}", request.Email, string.Join("; ", result.Errors.Select(e => e.Code)));
            throw new ValidationException(result.Errors.Select(e => e.Description));
        }

        if (!requireVerification)
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("User registered with email verification switched off; signing in directly. Email: {Email}", request.Email);
            }

            return await BuildAuthResponseAsync(user);
        }

        await TrySendVerificationEmailAsync(user);

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("User registered. Email: {Email}", request.Email);
        }

        return null;
    }

    private Task<bool> RequireEmailVerificationAsync() =>
        _featureFlags.IsEnabledAsync(FeatureFlagKeys.RequireEmailVerification, defaultValue: true);

    public async Task<AuthResponse> VerifyEmailAsync(VerifyEmailRequest request)
    {
        string code = NormalizeCode(request.Code)
            ?? throw new ValidationException("Enter the 6-digit code from your email.");

        ApplicationUser? user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // The same answer a wrong code gets, so this endpoint cannot be used
            // to find out which addresses have accounts.
            _logger.LogWarning("Email verification attempt for unrecognised email");
            throw new ValidationException(InvalidCodeMessage);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Email verification blocked — account is locked out. Email: {Email}", user.Email);
            }

            throw new UnauthorizedException(TooManyCodesMessage);
        }

        bool confirmed;
        if (IsDevelopmentCode(code))
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Development verification code accepted for {Email}", user.Email);
            }

            user.EmailConfirmed = true;
            confirmed = (await _userManager.UpdateAsync(user)).Succeeded;
        }
        else
        {
            confirmed = (await _userManager.ConfirmEmailAsync(user, code)).Succeeded;
        }

        if (!confirmed)
        {
            // A six-digit code is small enough to guess, so wrong codes count
            // toward the same lockout wrong passwords do.
            await _userManager.AccessFailedAsync(user);
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Email verification failed — wrong or expired code. Email: {Email}", user.Email);
            }

            throw new ValidationException(InvalidCodeMessage);
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Email verified successfully. Email: {Email}", user.Email);
        }

        return await BuildAuthResponseAsync(user);
    }

    public async Task ResendVerificationEmailAsync(ResendVerificationEmailRequest request)
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(request.Email);

        // Return silently if user does not exist or is already confirmed to avoid email enumeration
        if (user is null || user.EmailConfirmed)
        {
            return;
        }

        // Kept quiet on failure for the same reason the unknown-address case is:
        // a 500 here only for addresses that exist would answer the enumeration
        // question the silent return above is there to avoid.
        if (await TrySendVerificationEmailAsync(user))
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogInformation("Verification code resent. Email: {Email}", request.Email);
            }
        }
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(request.Email);

        // Return silently when user not found to prevent email enumeration
        if (user is null)
        {
            return;
        }

        string rawToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        string encodedToken = Uri.EscapeDataString(rawToken);
        string resetUrl = $"{_emailSettings.BaseUrl}/reset-password?userId={user.Id}&token={encodedToken}";

        string subject = "Reset your Parrot password";
        string body = $"""
            <h2>Password Reset</h2>
            <p>We received a request to reset your Parrot account password.</p>
            <p><a href="{resetUrl}">Reset Password</a></p>
            <p>This link expires after use. If you did not request a password reset, you can safely ignore this email.</p>
            """;

        try
        {
            await _emailService.SendEmailAsync(user.Email!, subject, body);
        }
        catch (Exception ex)
        {
            // Silent for the same reason the unknown-address path is.
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogError(ex, "Could not send the password reset email to {Email}", user.Email);
            }

            return;
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Password reset email sent. Email: {Email}", request.Email);
        }
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        ApplicationUser? user = await _userManager.FindByIdAsync(request.UserId);
        if (user is null)
        {
            _logger.LogWarning("Password reset attempt for unknown UserId: {UserId}", request.UserId);
            throw new NotFoundException(AuthMessages.ResetAccountNotFound);
        }

        IdentityResult result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Password reset failed: {Errors}. Email: {Email}", string.Join("; ", result.Errors.Select(e => e.Code)), user.Email);
            }

            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                throw new ValidationException(AuthMessages.ResetLinkExpired);
            }

            throw new ValidationException(result.Errors.Select(e => e.Description));
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Password reset successfully. Email: {Email}", user.Email);
        }
    }

    public async Task ChangePasswordAsync(string userId, ChangePasswordRequest request)
    {
        ApplicationUser? user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            _logger.LogWarning("Change password attempt for unknown UserId: {UserId}", userId);
            throw new NotFoundException("User not found.");
        }

        IdentityResult result = await _userManager.ChangePasswordAsync(
            user, request.CurrentPassword, request.NewPassword);

        if (!result.Succeeded)
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogWarning("Change password failed: {Errors}. Email: {Email}", string.Join("; ", result.Errors.Select(e => e.Code)), user.Email);
            }

            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            {
                throw new ValidationException("Current password is incorrect.");
            }

            throw new ValidationException(result.Errors.Select(e => e.Description));
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Password changed successfully. Email: {Email}", user.Email);
        }
    }

    public async Task<AuthResponse> ExternalLoginAsync(string email, string provider, string providerKey)
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(email);

        if (user is null)
        {
            // No business name is carried over: a Google profile name is a
            // person's, not the company's. The setup step after sign-in asks.
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
            };

            IdentityResult createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                _logger.LogError("External provider user creation failed for {Email} via {Provider}", email, provider);
                throw new ValidationException("An error occurred while creating the user.");
            }

            await _userManager.AddLoginAsync(user, new UserLoginInfo(provider, providerKey, provider));

            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogInformation("User created via {Provider}. Email: {Email}", provider, email);
            }
        }
        else if (await _userManager.IsLockedOutAsync(user))
        {
            throw new UnauthorizedException(AuthMessages.AccountLocked);
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("User signed in via {Provider}. Email: {Email}", provider, email);
        }

        return await BuildAuthResponseAsync(user);
    }

    // The account row is already committed by the time the verification code
    // goes out, so letting an SMTP fault bubble up reports failure for a sign-up
    // that actually succeeded, and the address is then unusable: registering
    // again conflicts and signing in is refused as unverified. Swallow the fault
    // and let the caller decide; the verify-email screen can resend.
    private async Task<bool> TrySendVerificationEmailAsync(ApplicationUser user)
    {
        if (_env.IsDevelopment() && !_emailSettings.SendInDevelopment)
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogInformation("Verification email skipped in Development. Email: {Email}", user.Email);
            }

            return false;
        }

        try
        {
            await SendVerificationEmailAsync(user);
            return true;
        }
        catch (Exception ex)
        {
            using (_logger.BeginEntityScope("User", user.Id))
            {
                _logger.LogError(ex, "Could not send the verification email to {Email}", user.Email);
            }

            return false;
        }
    }

    private async Task SendVerificationEmailAsync(ApplicationUser user)
    {
        // Identity's email token provider: six digits tied to the user's security
        // stamp, good for a few minutes. The development code, when configured,
        // is what the email carries too, so a local mailbox shows the same thing
        // the verify screen accepts.
        string code = DevelopmentCode() ?? await _userManager.GenerateEmailConfirmationTokenAsync(user);
        string verifyUrl = $"{_emailSettings.BaseUrl}/verify-email?email={Uri.EscapeDataString(user.Email!)}";

        string subject = "Your Parrot verification code";
        string body = $"""
            <h2>Verify your email</h2>
            <p>Enter this code in Parrot to verify your email address:</p>
            <p style="font-size:28px;font-weight:700;letter-spacing:6px;font-family:monospace">{code}</p>
            <p>It expires in 5 minutes.</p>
            <p><a href="{verifyUrl}">Open Parrot to enter the code</a></p>
            <p>If you did not create a Parrot account, you can safely ignore this email.</p>
            """;

        await _emailService.SendEmailAsync(user.Email!, subject, body);
    }

    // One field on the sign-in form, so a person never has to say which kind of
    // detail they are typing. An address goes through Identity's own finder; a
    // phone is one comparison against both of the shapes it can be written in —
    // the national column on its own, and that column behind its country code.
    private async Task<(ApplicationUser? User, string IdentifierKind)> FindByIdentifierAsync(string identifier)
    {
        if (identifier.Contains('@', StringComparison.Ordinal))
        {
            return (await _userManager.FindByEmailAsync(identifier), "email");
        }

        string? phone = PhoneNormalizer.SearchValue(identifier);
        if (phone is null)
        {
            return (null, "phone");
        }

        ApplicationUser? user = _userManager.Users.FirstOrDefault(u =>
            u.PhoneNumber == phone || (u.PhoneDialCode + u.PhoneNumber) == phone);

        return (user, "phone");
    }

    public async Task<AuthResponse> SwitchWorkspaceAsync(string userId, string workspaceId)
    {
        ApplicationUser user = await _userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("Account not found.");

        // Throws not-found for a workspace the user neither owns nor belongs
        // to, before anything is saved or issued.
        WorkspaceAccess access = await _workspaces.GetAccessibleAsync(userId, workspaceId);
        Workspace workspace = access.Workspace;

        if (user.LastWorkspaceId != workspace.Id)
        {
            user.LastWorkspaceId = workspace.Id;
            await _userManager.UpdateAsync(user);
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Switched to workspace {WorkspaceId}", workspace.Id);
        }

        return BuildAuthResponse(user, access);
    }

    private async Task<AuthResponse> BuildAuthResponseAsync(ApplicationUser user) =>
        BuildAuthResponse(user, await _workspaces.ResolveForSignInAsync(user));

    // A session works in one workspace at a time. Switching issues a new token
    // rather than letting a request name the workspace, so every request is
    // scoped by a claim that was checked against access when it was signed.
    // Access is checked again on every request, so a removed member's token
    // stops working at once.
    private AuthResponse BuildAuthResponse(ApplicationUser user, WorkspaceAccess access)
    {
        Workspace workspace = access.Workspace;
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email!),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(WorkspaceClaims.WorkspaceId, workspace.Id),
            new Claim(WorkspaceClaims.WorkspaceRole, access.Role),
            new Claim(WorkspaceClaims.Permissions, WorkspacePermissions.Abbreviate(access)),
            new Claim(WorkspaceClaims.Channels, WorkspaceChannels.Abbreviate(access)),
        ];

        if (!string.IsNullOrEmpty(workspace.Name))
        {
            claims.Add(new Claim(WorkspaceClaims.WorkspaceName, workspace.Name));
        }

        // The catalog service keys every row to a tenant and reads it from this claim
        // alone. Each workspace has its own catalog, so the workspace id is the catalog
        // tenant id; the owner administers it and a member gets the scopes their
        // catalog permissions add up to. The AI agent gets catalog:ai-tools through
        // a separate service token, never through a user session
        // (docs/catalog/adr/ADR-003-auth-tenant-and-scopes.md).
        claims.Add(new Claim(CatalogTenantClaim, workspace.Id));
        string catalogScopes = WorkspacePermissions.CatalogScopes(access);
        if (catalogScopes.Length > 0)
        {
            claims.Add(new Claim(ScopeClaim, catalogScopes));
        }

        DateTime expiresAt = _jwtHelper.GetDefaultExpiration();
        string token = _jwtHelper.GenerateToken(claims, expiresAt);

        return new AuthResponse(
            token,
            expiresAt,
            user.Id,
            user.Email!,
            workspace.Name,
            workspace.LogoUrl,
            PhoneNormalizer.ToDisplay(user.PhoneDialCode, user.PhoneNumber),
            workspace.Id,
            access.Role,
            WorkspacePermissions.Effective(access));
    }

    // People type "222 222" as readily as "222222"; only the digits matter.
    private static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 6 ? digits : null;
    }

    // Both the environment and the setting have to agree; Program.cs refuses to
    // start with the setting present anywhere but Development as well.
    private string? DevelopmentCode() =>
        _env.IsDevelopment() && !string.IsNullOrEmpty(_authSettings.DevelopmentVerificationCode)
            ? _authSettings.DevelopmentVerificationCode
            : null;

    private bool IsDevelopmentCode(string code) =>
        DevelopmentCode() is { } fixedCode && string.Equals(code, fixedCode, StringComparison.Ordinal);
}
