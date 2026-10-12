using Microsoft.AspNetCore.DataProtection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// ASP.NET Data Protection implementation; its purpose strings keep TikTok credentials and OAuth states
// on their own keys, apart from each other and from the other platforms.
public sealed class TikTokCredentialProtector(IDataProtectionProvider provider) : ITikTokCredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Parrot.TikTok.Credentials.v1");
    private readonly ITimeLimitedDataProtector _state = provider.CreateProtector("Parrot.TikTok.OAuthState.v1").ToTimeLimitedDataProtector();
    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
    public string ProtectState(string value, TimeSpan lifetime) => _state.Protect(value, lifetime);
    public string UnprotectState(string protectedState) => _state.Unprotect(protectedState);
}
