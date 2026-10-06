using System.Net;

namespace Parrot.Domain.Exceptions;

// A dependency the request needs is down, so the caller should try again
// later. Meta resends a WhatsApp webhook that doesn't get a 200.
public class ServiceUnavailableException : DomainException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.ServiceUnavailable;
    public override string ErrorCode => "SERVICE_UNAVAILABLE";

    public ServiceUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
