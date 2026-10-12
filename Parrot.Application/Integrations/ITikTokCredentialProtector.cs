namespace Parrot.Application.Integrations;

// Encrypts TikTok App Secrets and tokens at rest, and seals the OAuth state that travels through TikTok.
public interface ITikTokCredentialProtector
{
    // Encrypts a plaintext credential for storage.
    string Protect(string value);

    // Decrypts a stored credential; throws CryptographicException if the key ring rotated.
    string Unprotect(string protectedValue);

    // Seals the OAuth state so it cannot be read or altered, and stops being accepted after lifetime.
    string ProtectState(string value, TimeSpan lifetime);

    // Opens a sealed state; throws CryptographicException if it was altered or has expired.
    string UnprotectState(string protectedState);
}
