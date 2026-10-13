using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public static class WhatsAppDataProtectionExtensions
{
    // readOnly is for services that only decrypt (the message worker): they share the API's key ring
    // and must never write a key of their own into it.
    public static IServiceCollection AddWhatsAppCredentialProtection(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment, bool readOnly = false)
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
        if (readOnly)
        {
            builder.DisableAutomaticKeyGeneration();
        }

        try
        {
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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Data Protection certificate load failed ({certificatePath}): {ex}");
        }

        services.AddSingleton<IWhatsAppTokenProtector, WhatsAppTokenProtector>();
        services.AddSingleton<IFacebookCredentialProtector, FacebookCredentialProtector>();
        services.AddSingleton<ITikTokCredentialProtector, TikTokCredentialProtector>();
        services.AddSingleton<IInstagramCredentialProtector, InstagramCredentialProtector>();
        return services;
    }
}
