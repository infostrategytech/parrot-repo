using Microsoft.AspNetCore.DataProtection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// ASP.NET Data Protection implementation; its purpose string pins Facebook credentials to their own key.
public sealed class FacebookCredentialProtector(IDataProtectionProvider provider) : IFacebookCredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Parrot.Facebook.Credentials.v1");
    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
