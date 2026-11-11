namespace Parrot.Web.Features.Auth.ResetPassword.Services;

/// <summary>Optional local preview of the link normally delivered by email; never a production verification path.</summary>
public interface IPasswordResetPreview { string? LinkFor(string? userId); }
public sealed class UnavailablePasswordResetPreview : IPasswordResetPreview { public string? LinkFor(string? userId) => null; }
