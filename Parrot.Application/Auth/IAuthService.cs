using Parrot.Application.DTOs.Auth;

namespace Parrot.Application.Auth;

public interface IAuthService
{
    /// <summary>A session when verification is switched off; null when a code is on its way.</summary>
    Task<AuthResponse?> RegisterAsync(RegisterRequest request);

    Task<AuthResponse> LoginAsync(LoginRequest request);

    Task<AuthResponse> VerifyEmailAsync(VerifyEmailRequest request);

    Task ResendVerificationEmailAsync(ResendVerificationEmailRequest request);

    Task ForgotPasswordAsync(ForgotPasswordRequest request);

    Task ResetPasswordAsync(ResetPasswordRequest request);

    Task ChangePasswordAsync(string userId, ChangePasswordRequest request);

    Task<AuthResponse> ExternalLoginAsync(string email, string displayName, string provider, string providerKey);
}
