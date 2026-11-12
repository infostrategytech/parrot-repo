namespace Parrot.Web.Features.Profile;

/// <summary>A code checked: right (no error), wrong (try again), or expired or used up (Locked: send a new one).</summary>
public sealed record CodeCheck(string? Error = null, bool Locked = false)
{
    public static readonly CodeCheck Ok = new();
}

/// <summary>What went wrong sending the code, against the field it belongs to.</summary>
public sealed record EmailChangeProblem(string Message, bool Password = false);

/// <summary>
/// Changing the email you sign in with, as big sites do it (designer, 4 November 2026): your current password with
/// the new address proves it's you, a code to the new address proves it's yours, and the old address is told
/// afterwards. Until the code is entered the old address keeps working and the change waits, so it can be
/// finished or cancelled later. No route for this yet (profile-backend-requests).
/// </summary>
public interface IEmailChange
{
    bool Available { get; }

    /// <summary>The address waiting for its code, or null.</summary>
    Task<string?> PendingAsync(CancellationToken ct = default);

    /// <summary>Checks the password and sends a code to the new address. Returns what went wrong (a wrong password,
    /// not an email, the same address, already on another account), or null.</summary>
    Task<EmailChangeProblem?> StartAsync(string newEmail, string password, CancellationToken ct = default);

    /// <summary>A new code to the address that's waiting. Returns what went wrong, or null.</summary>
    Task<string?> ResendAsync(CancellationToken ct = default);

    /// <summary>Right code: the address is changed and the session signs in with it from now on.</summary>
    Task<CodeCheck> ConfirmAsync(string code, CancellationToken ct = default);

    /// <summary>Drops the waiting change; the current address stays.</summary>
    Task CancelAsync(CancellationToken ct = default);
}

/// <summary>No email-change API yet: Profile says so and offers nothing it can't do.</summary>
public sealed class UnavailableEmailChange : IEmailChange
{
    public bool Available => false;
    public Task<string?> PendingAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<EmailChangeProblem?> StartAsync(string newEmail, string password, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> ResendAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CodeCheck> ConfirmAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CancelAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>
/// Your own photo, kept apart from the business logo. The API keeps one image today, the logo, so writing a
/// photo there would replace the logo for everyone: until it has a field of its own this is unavailable outside
/// the demo, and Profile shows initials (profile-backend-requests).
/// </summary>
public interface IProfilePhoto
{
    bool Available { get; }

    /// <summary>The photo once read or changed, or null. The rail's account shows it too.</summary>
    string? Url { get; }

    event Action? Changed;

    Task<string?> ReadAsync(CancellationToken ct = default);

    /// <summary>Uploads and keeps it straight away. PNG, JPG or WebP, up to 2 MB.</summary>
    Task<string> SaveAsync(string contentType, byte[] bytes, CancellationToken ct = default);

    Task RemoveAsync(CancellationToken ct = default);
}

public sealed class UnavailableProfilePhoto : IProfilePhoto
{
    public bool Available => false;
    public string? Url => null;
    public event Action? Changed { add { } remove { } }
    public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string> SaveAsync(string contentType, byte[] bytes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>The one rule for photos everywhere: PNG, JPG or WebP, up to 2 MB (inside what the API accepts).</summary>
public static class PhotoRules
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly string[] Types = ["image/png", "image/jpeg", "image/webp"];
    public const string Accept = "image/png,image/jpeg,image/webp";

    public static string? Problem(string contentType, long size) =>
        !Types.Contains(contentType) ? "Use a PNG, JPG or WebP image."
        : size > MaxBytes ? "That image is over 2 MB. Choose a smaller one."
        : null;
}
