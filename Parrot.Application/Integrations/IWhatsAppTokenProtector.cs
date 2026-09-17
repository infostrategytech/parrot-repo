namespace Parrot.Application.Integrations;

public interface IWhatsAppTokenProtector
{
    string Protect(string token);
    string Unprotect(string encryptedToken);
}
