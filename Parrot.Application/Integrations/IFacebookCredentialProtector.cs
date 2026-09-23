namespace Parrot.Application.Integrations;

public interface IFacebookCredentialProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}
