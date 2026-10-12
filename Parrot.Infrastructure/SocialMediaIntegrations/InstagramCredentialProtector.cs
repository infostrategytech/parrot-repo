using Microsoft.AspNetCore.DataProtection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// ASP.NET Data Protection; its purpose string pins Instagram credentials to their own key.
public sealed class InstagramCredentialProtector(IDataProtectionProvider provider) : IInstagramCredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Parrot.Instagram.Credentials.v1");
    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
