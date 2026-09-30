namespace Parrot.Web.Features.Auth.State;

/// <summary>
/// What the signed-out screens carry between them, in memory for this circuit.
///
/// The email a code was sent to used to travel in the address bar
/// (/verify-email?email=...), which puts it in browser history and server logs.
/// It is held here instead. A refresh loses it; the Verify page then asks for
/// the address, so nobody is stuck. Never holds a password.
/// </summary>
public sealed class AuthFormState
{
    /// <summary>The last email typed on any signed-out form, so moving between them keeps it.</summary>
    public string? EnteredEmail { get; private set; }

    /// <summary>The address a verification code was just sent to.</summary>
    public string? PendingVerificationEmail { get; private set; }

    public void Remember(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email)) EnteredEmail = email.Trim();
    }

    public void CodeSentTo(string email)
    {
        PendingVerificationEmail = email.Trim();
        Remember(email);
    }

    public void Verified() => PendingVerificationEmail = null;

    /// <summary>"ad•••@shop.ng": enough to recognise, not enough to read over a shoulder.</summary>
    public static string Mask(string email)
    {
        var parts = email.Split('@', 2);
        if (parts.Length < 2 || parts[0].Length == 0) return email;
        var visible = parts[0].Length > 1 ? parts[0][..2] : parts[0];
        return $"{visible}•••@{parts[1]}";
    }
}
