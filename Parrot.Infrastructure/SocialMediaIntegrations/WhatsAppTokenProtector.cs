using Microsoft.AspNetCore.DataProtection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public sealed class WhatsAppTokenProtector : IWhatsAppTokenProtector
{
    private readonly IDataProtector _protector;

    public WhatsAppTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Parrot.WhatsApp.VerifyToken.v1");
    }

    public string Protect(string token) => _protector.Protect(token);

    public string Unprotect(string encryptedToken) => _protector.Unprotect(encryptedToken);
}
