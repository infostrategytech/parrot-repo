namespace Parrot.Application.Auth;

public interface IPasswordBreachChecker
{
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default);
}
