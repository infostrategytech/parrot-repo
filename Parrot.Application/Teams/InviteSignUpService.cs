using Microsoft.AspNetCore.Identity;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.DTOs.Teams;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Teams;

/// <summary>
/// One login for everything: someone invited who has no account gets one
/// from the invite link, joins the team and is signed in, in one step. The
/// same account then signs in to every team they join and to any business
/// they start themselves.
/// </summary>
public interface IInviteSignUpService
{
    Task<AuthResponse> SignUpAndAcceptAsync(string token, InviteSignUpRequest request);
}

public sealed class InviteSignUpService(
    ITeamService team,
    IAuthService auth,
    UserManager<ApplicationUser> userManager,
    IAppLogger<InviteSignUpService> logger) : IInviteSignUpService
{
    public async Task<AuthResponse> SignUpAndAcceptAsync(string token, InviteSignUpRequest request)
    {
        TeamInvite invite = await team.GetPendingInviteAsync(token);

        string firstName = request.FirstName.Trim();
        if (firstName.Length == 0)
        {
            throw new ValidationException("Enter your first name.");
        }

        string? lastName = string.IsNullOrWhiteSpace(request.LastName) ? null : request.LastName.Trim();
        ApplicationUser? existing = await userManager.FindByEmailAsync(invite.Email);

        // A proved login signs in instead: the link must never set the password
        // of an account its owner has already secured.
        if (existing is { EmailConfirmed: true })
        {
            throw new ConflictException($"An account already uses {invite.Email}. Sign in to accept the invite.");
        }

        ApplicationUser user = existing is null
            ? await CreateAsync(invite, firstName, lastName, request.Password)
            : await ClaimAsync(existing, invite, firstName, lastName, request.Password);

        string workspaceId = await team.AcceptInviteAsync(user.Id, token);
        return await auth.SwitchWorkspaceAsync(user.Id, workspaceId);
    }

    private async Task<ApplicationUser> CreateAsync(TeamInvite invite, string firstName, string? lastName, string password)
    {
        ApplicationUser user = new()
        {
            UserName = invite.Email,
            Email = invite.Email,
            FirstName = firstName,
            LastName = lastName,
            // The invite was emailed to this address and its link opened: that
            // is what a verification code would prove.
            EmailConfirmed = true,
        };

        ThrowIfFailed(await userManager.CreateAsync(user, password), invite);

        using (logger.BeginEntityScope("User", user.Id))
        {
            logger.LogInformation("Account made from invite {InviteId}", invite.Id);
        }

        return user;
    }

    // An account under this address that was never proved: whoever made it may
    // not own the inbox, and the link shows that this person does. It becomes
    // theirs, as a password reset sent to the address would make it.
    private async Task<ApplicationUser> ClaimAsync(ApplicationUser user, TeamInvite invite, string firstName, string? lastName, string password)
    {
        // A reset rather than remove-then-add: the new password is checked against
        // the policy before anything changes, so a weak one leaves the account as it was.
        string reset = await userManager.GeneratePasswordResetTokenAsync(user);
        ThrowIfFailed(await userManager.ResetPasswordAsync(user, reset, password), invite);

        user.FirstName = firstName;
        user.LastName = lastName;
        user.EmailConfirmed = true;
        ThrowIfFailed(await userManager.UpdateAsync(user), invite);

        using (logger.BeginEntityScope("User", user.Id))
        {
            logger.LogWarning("Unproved account under the invited address claimed through invite {InviteId}", invite.Id);
        }

        return user;
    }

    private void ThrowIfFailed(IdentityResult result, TeamInvite invite)
    {
        if (result.Succeeded)
        {
            return;
        }

        // Almost always the password policy; its own wording says how to fix it.
        logger.LogWarning("Invite sign-up failed for invite {InviteId}: {Errors}", invite.Id, string.Join("; ", result.Errors.Select(e => e.Code)));
        throw new ValidationException(result.Errors.Select(e => e.Description));
    }
}
