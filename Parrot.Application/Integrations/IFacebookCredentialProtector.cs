namespace Parrot.Application.Integrations;

// Encrypts Facebook verify tokens and Page access tokens at rest, mirroring IWhatsAppTokenProtector.
public interface IFacebookCredentialProtector
{
    // Encrypts a plaintext credential for storage.
    string Protect(string value);

    // Decrypts a stored credential; throws CryptographicException if the key ring rotated.
    string Unprotect(string protectedValue);
}
