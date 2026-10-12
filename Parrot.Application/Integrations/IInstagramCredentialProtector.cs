namespace Parrot.Application.Integrations;

// Encrypts Instagram verify tokens, access tokens and app secrets at rest.
public interface IInstagramCredentialProtector
{
    string Protect(string value);

    // Throws CryptographicException if the key ring rotated.
    string Unprotect(string protectedValue);
}
