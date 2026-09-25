using System.Net;

namespace Parrot.Domain.Exceptions;

// Only ever raised after the password has checked out, so it says nothing
// about whether an address is registered: a wrong password on an unverified
// account still gets the generic unauthorised answer.
public class EmailNotVerifiedException : DomainException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.Forbidden;
    public override string ErrorCode => "EMAIL_NOT_VERIFIED";

    public EmailNotVerifiedException(string message = "Please verify your email address before signing in.")
        : base(message)
    {
    }
}
