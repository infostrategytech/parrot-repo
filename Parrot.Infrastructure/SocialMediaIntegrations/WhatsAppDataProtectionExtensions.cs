using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public static class WhatsAppDataProtectionExtensions
{
    public static IServiceCollection AddWhatsAppCredentialProtection(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        string? keyRingPath = configuration["DataProtection:KeyRingPath"];
        string? certificatePath = configuration["DataProtection:CertificatePath"];
        if (!isDevelopment && (string.IsNullOrWhiteSpace(keyRingPath) || string.IsNullOrWhiteSpace(certificatePath)))
        {
            throw new InvalidOperationException("Configure DataProtection:KeyRingPath on durable storage and DataProtection:CertificatePath outside Development.");
        }

        keyRingPath = string.IsNullOrWhiteSpace(keyRingPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parrot", "DataProtection-Keys")
            : keyRingPath;

        IDataProtectionBuilder builder = services.AddDataProtection()
            .SetApplicationName("Parrot")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            X509KeyStorageFlags keyStorageFlags = OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.DefaultKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, configuration["DataProtection:CertificatePassword"], keyStorageFlags);
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException("The Data Protection certificate must include its private key.");
            }

            builder.ProtectKeysWithCertificate(certificate);
        }

        services.AddSingleton<IWhatsAppTokenProtector, WhatsAppTokenProtector>();
        return services;
    }
}
