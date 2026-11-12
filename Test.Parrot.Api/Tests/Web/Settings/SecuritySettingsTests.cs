using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Settings;
using Parrot.Web.Features.Auth.ForgotPassword.Models;
using Parrot.Web.Features.Auth.ForgotPassword.Services;
using Parrot.Web.Features.Auth.ResetPassword.Models;
using Parrot.Web.Features.Auth.ResetPassword.Services;
using Parrot.Web.Features.Auth.ResetPassword.State;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Profile;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Real components + public service boundaries, not a dependency on demo records or test codes in the UI.
public sealed class SecuritySettingsTests
{
    [Fact]
    public async Task TwoCardsOfLinesAsNotificationsWithoutPreviewWording()
    {
        await using var page = await Page.Open();
        var html = await page.Html();
        // Two standard Settings cards of lines, drawn as Notifications draws its settings.
        Assert.Equal(2, html.Split("class=\"settings-card\"").Length - 1);
        Assert.Contains("security__line", html);
        Assert.DoesNotContain("<h2", html);
        Assert.DoesNotContain("preview", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simulated", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Two-factor authentication", html);
        Assert.Contains("Where you&#x27;re signed in", html);
        Assert.Contains("This device", html);
        Assert.DoesNotContain("/dashboard/settings/activity", html); // The destination is still a placeholder.
        Assert.DoesNotContain("Recent security activity", html);
        Assert.DoesNotContain("security-current-password", html);
    }

    [Fact]
    public async Task MissingRealCapabilityDoesNotInventDevicesOrProtectionStatus()
    {
        await using var page = await Page.Open(new UnavailableAccountSecurity());
        var html = await page.Html();
        Assert.Contains("isn&#x27;t available yet", html);
        Assert.Contains("aren&#x27;t available yet", html);
        Assert.DoesNotContain("This device", html);
        Assert.DoesNotContain("status-badge", html);
        Assert.Contains("Change password", html); // Existing password endpoint stays available.
    }

    [Fact]
    public async Task FailedSecurityReadRequiresRetryAndDoesNotDisableExistingPasswordChange()
    {
        var api = new SecurityApi { FailRead = true };
        await using var page = await Page.Open(api);
        Assert.Contains("before making changes", await page.Html());
        await page.Call(page.Root, "OpenTwoFactor");
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
        Assert.Contains("Change password", await page.Html());
        api.FailRead = false;
        await page.Call(page.Root, "LoadAsync");
        Assert.Contains("This device", await page.Html());
    }

    [Fact]
    public async Task GoogleOnlyAccountDoesNotRequestAPasswordItDoesNotHave()
    {
        var api = new SecurityApi();
        api.Info = api.Info with { HasPassword = false, SignInProviders = ["Google"] };
        await using var page = await Page.Open(api);
        Assert.Contains("You sign in with Google", await page.Html());
        Assert.DoesNotContain("Change password", await page.Html());
        await page.Call(page.Root, "OpenTwoFactor");
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
    }

    [Theory]
    [InlineData("", "abcdefgh", "abcdefgh")]
    [InlineData("current123", "short", "short")]
    [InlineData("current123", "abcdefgh", "different")]
    [InlineData("current123", "current123", "current123")]
    public void InvalidPasswordChangesAreRejectedBeforeSending(string current, string next, string confirmation)
        => Assert.NotNull(SecurityText.NewPasswordProblem(current, next, confirmation));

    [Fact]
    public async Task PasswordUsesExistingEndpointAndClearsSecretsAfterSuccess()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_current", "current123");
        await page.Set(dialog, "_new", "Next-password123");
        await page.Set(dialog, "_confirm", "Next-password123");
        await page.Call(dialog, "ChangeAsync");
        Assert.Equal("/api/auth/change-password", Assert.Single(page.Http.Paths));
        Assert.Equal("", page.Value<string>(dialog, "_current"));
        Assert.Equal("", page.Value<string>(dialog, "_new"));
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen"));
    }

    [Fact]
    public async Task PasswordFailureStaysOpenForRetry()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_current", "current123"); await page.Set(dialog, "_new", "Next-password123"); await page.Set(dialog, "_confirm", "Next-password123");
        page.Http.Fail = true;
        await page.Call(dialog, "ChangeAsync");
        Assert.True(page.Value<bool>(page.Root, "_passwordOpen"));
        Assert.Contains("Check your connection", await page.Html());
        page.Http.Fail = false;
        await page.Call(dialog, "ChangeAsync");
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen"));
    }

    [Fact]
    public async Task TwoFactorOnlyTurnsOnAfterVerificationAndRecoveryCodesMustBeAcknowledged()
    {
        await using var page = await Page.Open();
        var dialog = await page.BeginFactor(TwoFactorMethod.App);
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
        Assert.Contains("data:image/png;base64,", await page.Html());
        await page.Set(dialog, "_code", "123456");
        page.Api.FailConfirm = true;
        await page.Call(dialog, "SubmitAsync");
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
        Assert.Contains("Check your connection", await page.Html());
        page.Api.FailConfirm = false;
        await page.Call(dialog, "SubmitAsync");
        Assert.True(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
        Assert.Contains("Save your recovery codes", await page.Html());
        Assert.Equal("", page.Value<string>(dialog, "_password"));
        Assert.Null(page.Value<AuthenticatorSetup?>(dialog, "_setup"));
        await page.Call(dialog, "CloseAsync");
        Assert.NotNull(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
        var location = new LocationChangingContext { TargetLocation = "http://localhost/dashboard" };
        await page.Call(dialog, "GuardCodes", location);
        Assert.Contains("Leave without saving your codes?", await page.Html());
        Assert.True(page.Value<bool>(dialog, "_confirmLeaveCodes"));
        await page.Call(dialog, "StayWithCodes"); // "Go back" in the leave Dialog: the codes again, not a trapped Close.
        Assert.False(page.Value<bool>(dialog, "_confirmLeaveCodes"));
        await page.Set(dialog, "_saved", true);
        await page.Call(dialog, "SubmitAsync");
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
    }

    [Fact]
    public async Task WrongAndExpiredSetupCodesHaveDistinctRecovery()
    {
        await using var page = await Page.Open();
        var dialog = await page.BeginFactor(TwoFactorMethod.App);
        await page.Set(dialog, "_code", "123456");
        page.Api.CodeProblem = new("Wrong code.", SecurityField.Code);
        await page.Call(dialog, "SubmitAsync");
        Assert.False(page.Value<bool>(dialog, "_restartRequired"));
        page.Api.CodeProblem = new("Setup expired.", SecurityField.Code, true);
        await page.Call(dialog, "SubmitAsync");
        Assert.True(page.Value<bool>(dialog, "_restartRequired"));
        Assert.Contains("Start again", await page.Html());
        await page.Call(dialog, "RestartAsync");
        Assert.Null(page.Value<AuthenticatorSetup?>(dialog, "_setup"));
        Assert.False(page.Value<bool>(dialog, "_restartRequired"));
        Assert.Equal(1, page.Api.Cancels);
    }

    [Fact]
    public async Task CancellingAnUnverifiedSetupDoesNotEnableProtection()
    {
        await using var page = await Page.Open();
        var dialog = await page.BeginFactor(TwoFactorMethod.App);
        await page.Call(dialog, "CloseAsync");
        Assert.Equal(1, page.Api.Cancels);
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
    }

    [Fact]
    public async Task TurningOffNeedsVerificationAndOnlyUpdatesAfterAcknowledgement()
    {
        var api = new SecurityApi(); api.Info = api.Info with { TwoFactorEnabled = true, Methods = [TwoFactorMethod.App] };
        await using var page = await Page.Open(api);
        await page.Call(page.Root, "OpenTwoFactor");
        var dialog = page.Instance<TwoFactorDialog>();
        await page.Set(dialog, "_password", "current123");
        await page.Call(dialog, "SubmitAsync");
        Assert.Equal(0, api.Disables);
        await page.Set(dialog, "_code", "a-recovery-code");
        api.FailDisable = true;
        await page.Call(dialog, "SubmitAsync");
        Assert.True(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
        api.FailDisable = false;
        await page.Call(dialog, "SubmitAsync");
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
    }

    // Designer, 9 November 2026: recovery codes come with two-factor whichever way it's turned on, email included.
    [Fact]
    public async Task EmailCodesAreTheDefaultAndTurnOnWithRecoveryCodes()
    {
        await using var page = await Page.Open();
        var dialog = await page.BeginFactor();
        Assert.Equal(1, page.Api.EmailSetups);
        Assert.Equal(0, page.Api.AppSetups);
        Assert.Contains("Check your email", await page.Html());
        Assert.Contains("controlled@example.com", await page.Html());
        Assert.DoesNotContain("data:image/png;base64,", await page.Html());
        await page.Call(dialog, "ResendAsync", CancellationToken.None);
        Assert.Equal(1, page.Api.Resends);
        await page.Set(dialog, "_code", "123456");
        await page.Call(dialog, "SubmitAsync");
        var account = page.Value<AccountSecurityInfo>(page.Root, "_account");
        Assert.True(account.TwoFactorEnabled);
        Assert.True(account.Has(TwoFactorMethod.Email));
        Assert.Contains("Save your recovery codes", await page.Html());
        await page.Set(dialog, "_saved", true);
        await page.Call(dialog, "SubmitAsync");
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
        var html = System.Net.WebUtility.HtmlDecode(await page.Html());
        Assert.Contains("Sent to controlled@example.com", html);
        Assert.Contains("Recovery codes", html);
    }

    // Designer, 9 November 2026: both ways can be on, so one that can't be reached never locks anyone out.
    [Fact]
    public async Task TheOtherWayCanBeAddedWithoutNewRecoveryCodesAndOneOfTwoRemoved()
    {
        var api = new SecurityApi(); api.Info = api.Info with { TwoFactorEnabled = true, Methods = [TwoFactorMethod.Email] };
        await using var page = await Page.Open(api);
        var html = System.Net.WebUtility.HtmlDecode(await page.Html());
        Assert.Contains("Authenticator app", html);
        Assert.Contains("A way in if you can't get to your email.", html);
        Assert.DoesNotContain(">Remove<", html); // The only way on goes with Turn off, not Remove.

        await page.Call(page.Root, "OpenWay", TwoFactorAction.Add, (TwoFactorMethod?)TwoFactorMethod.App);
        var dialog = page.Instance<TwoFactorDialog>();
        Assert.Contains("Add an authenticator app", await page.Html());
        Assert.DoesNotContain("Get codes by", await page.Html()); // The way is already chosen.
        await page.Set(dialog, "_password", "current123"); await page.Call(dialog, "SubmitAsync");
        Assert.Equal(1, api.AppSetups);
        await page.Set(dialog, "_code", "123456"); await page.Call(dialog, "SubmitAsync");
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction")); // No codes to save: they came with the first way.
        Assert.True(page.Value<AccountSecurityInfo>(page.Root, "_account").Has(TwoFactorMethod.App));
        Assert.Contains(">Remove<", await page.Html());

        await page.Call(page.Root, "OpenWay", TwoFactorAction.Remove, (TwoFactorMethod?)TwoFactorMethod.Email);
        var remove = page.Instance<TwoFactorDialog>();
        Assert.Contains("Remove email codes?", await page.Html());
        await page.Set(remove, "_password", "current123"); await page.Call(remove, "SubmitAsync");
        Assert.Equal(1, api.Removals);
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").Has(TwoFactorMethod.Email));
    }

    [Fact]
    public async Task TurningOffEmailCodesNeedsOnlyThePassword()
    {
        var api = new SecurityApi(); api.Info = api.Info with { TwoFactorEnabled = true, Methods = [TwoFactorMethod.Email], RecoveryCodesRemaining = 0 };
        await using var page = await Page.Open(api);
        await page.Call(page.Root, "OpenTwoFactor");
        var dialog = page.Instance<TwoFactorDialog>();
        Assert.DoesNotContain("factor-verify-code", await page.Html());
        await page.Set(dialog, "_password", "current123");
        await page.Call(dialog, "SubmitAsync");
        Assert.Equal(1, api.Disables);
        Assert.False(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
    }

    [Fact]
    public async Task ReplacingRecoveryCodesWarnsBeforeDoingAnythingAndRequiresVerification()
    {
        var api = new SecurityApi(); api.Info = api.Info with { TwoFactorEnabled = true, Methods = [TwoFactorMethod.App] };
        await using var page = await Page.Open(api);
        Assert.Contains("Recovery codes", await page.Html());
        await page.Call(page.Root, "OpenFactor", TwoFactorAction.ReplaceCodes);
        var dialog = page.Instance<TwoFactorDialog>();
        Assert.Contains("Your old codes will stop working", await page.Html());
        Assert.Equal(0, api.Replacements);
        await page.Set(dialog, "_password", "current123"); await page.Set(dialog, "_code", "a-recovery-code");
        await page.Call(dialog, "SubmitAsync");
        Assert.Equal(1, api.Replacements);
        Assert.Contains("Save your recovery codes", await page.Html());
    }

    [Fact]
    public async Task FailedCopyDoesNotClaimRecoveryCodesWereCopied()
    {
        await using var page = await Page.Open();
        var dialog = await page.BeginFactor(TwoFactorMethod.App);
        page.Js.Module.CopyWorks = false;
        await page.Call(dialog, "CopyAsync", "controlled-secret");
        Assert.Contains("Select the text and copy it", page.Value<string>(dialog, "_copyStatus"));
        page.Js.Module.CopyWorks = true;
        await page.Call(dialog, "CopyAsync", "controlled-secret");
        Assert.Equal("Copied", page.Value<string>(dialog, "_copyStatus"));
        Assert.All(page.Js.Module.Calls, call => Assert.Equal("copyText", call));
    }

    [Fact]
    public async Task SessionFailureKeepsTheDeviceUntilRetrySucceeds()
    {
        await using var page = await Page.Open();
        var other = page.Api.Info.Sessions.Single(session => !session.Current);
        await page.Call(page.Root, "AskToRevoke", other);
        page.Api.FailRevoke = true;
        await page.Call(page.Root, "RevokeAsync");
        Assert.Equal(2, page.Value<AccountSecurityInfo>(page.Root, "_account").Sessions.Count);
        Assert.True(page.Value<bool>(page.Root, "_revokeOpen"));
        page.Api.FailRevoke = false;
        await page.Call(page.Root, "RevokeAsync");
        Assert.True(Assert.Single(page.Value<AccountSecurityInfo>(page.Root, "_account").Sessions).Current);
        Assert.False(page.Value<bool>(page.Root, "_revokeOpen"));
    }

    [Fact]
    public async Task AllOtherSessionsPreservesCurrentAndCurrentCannotBeTargeted()
    {
        await using var page = await Page.Open();
        await page.Call(page.Root, "AskToRevoke", page.Api.Info.Sessions.Single(session => session.Current));
        Assert.False(page.Value<bool>(page.Root, "_revokeOpen"));
        await page.Call(page.Root, "AskToRevoke", new object?[] { null });
        Assert.Contains("stay signed in here", await page.Html());
        await page.Call(page.Root, "RevokeAsync");
        Assert.True(Assert.Single(page.Value<AccountSecurityInfo>(page.Root, "_account").Sessions).Current);
        Assert.Equal(1, page.Api.BulkRevokes);
    }

    [Fact]
    public void SharedPasswordRulesStayCompatibleAndSetupCodesKeepLeadingZeros()
    {
        Assert.True(PasswordRules.MeetsAll("LongPassword12"));
        Assert.False(PasswordRules.MeetsAll("lowercase"));
        Assert.True(SecurityText.SixDigits("001234"));
        Assert.False(SecurityText.SixDigits("12345a"));
        Assert.False(SecurityText.SixDigits("12345"));
        Assert.Null(SecurityText.NewPasswordProblem("current123", "A-long-unique-password1", "A-long-unique-password1"));
        Assert.NotNull(SecurityText.NewPasswordProblem("current123", "a-long-unique-password", "a-long-unique-password"));
    }

    [Fact]
    public async Task EnterUsesAnAssociatedNativeSubmitButtonAndRespectsDisabledState()
    {
        await using var page = await Page.Open();
        await page.Call(page.Root, "OpenTwoFactor");
        var factor = page.Instance<TwoFactorDialog>();
        // A form, so the shared ContentModal (8 November 2026): its native form and footer submit button.
        var dialog = page.ActiveContentModal;
        Assert.Equal("factor-password", dialog.InitialFocusId);
        Assert.Contains("type=\"submit\"", await page.Html());
        var formId = page.Value<string>(dialog, "_formId");
        Assert.Contains($"<form id=\"{formId}\"", await page.Html());
        Assert.Contains($"form=\"{formId}\"", await page.Html());
        await page.Call(dialog, "SubmitAsync");
        Assert.Equal("Enter your password.", page.Value<string?>(factor, "_passwordError"));
        Assert.Null(page.Value<EmailCodeSetup?>(factor, "_emailSetup"));
        await page.Set(factor, "_password", "current123");
        await page.Call(dialog, "SubmitAsync");
        Assert.NotNull(page.Value<EmailCodeSetup?>(factor, "_emailSetup"));
        Assert.Equal("factor-email-code", page.ActiveContentModal.InitialFocusId);
        // The button stays live, as the app's other dialogs; pressing it without the code says what's missing.
        await page.Call(page.ActiveContentModal, "SubmitAsync");
        Assert.Equal("Enter the 6-digit code.", page.Value<string?>(factor, "_error"));
    }

    [Fact]
    public async Task ManualKeyIsProgressivelyRevealedWithSamePhoneInstructions()
    {
        await using var page = await Page.Open();
        var factor = await page.BeginFactor(TwoFactorMethod.App);
        Assert.Contains("data:image/png;base64,", await page.Html());
        Assert.DoesNotContain("CONTROLLED-KEY", await page.Html());
        await page.Set(factor, "_manual", true);
        Assert.Contains("CONTROLLED-KEY", await page.Html());
        Assert.Contains("Then enter the 6-digit code it shows.", await page.Html());
        Assert.Equal("factor-setup-code", page.ActiveContentModal.InitialFocusId);
    }

    [Fact]
    public async Task RecoveryCodesHaveOneFinishActionAndAHonestExitThatDoesNotDisableProtection()
    {
        await using var page = await Page.Open();
        var factor = await page.BeginFactor(TwoFactorMethod.App);
        await page.Set(factor, "_code", "123456"); await page.Call(factor, "SubmitAsync");
        Assert.Equal("factor-code-instructions", page.ActiveContentModal.InitialFocusId);
        Assert.DoesNotContain("id=\"factor-cancel\"", await page.Html()); // One way on: Done.
        Assert.Contains("Download codes", await page.Html());
        await page.Call(factor, "CloseAsync");
        // Leaving unsaved is a decision: a Dialog of its own over the codes, Go back first.
        Assert.Equal("factor-leave-back", page.ActiveDialog.FocusTargetId);
        Assert.Contains("Two-factor authentication stays on", await page.Html());
        await page.Call(factor, "LeaveCodesAsync"); // Explicit Leave without saving.
        Assert.Null(page.Value<TwoFactorAction?>(page.Root, "_factorAction"));
        Assert.True(page.Value<AccountSecurityInfo>(page.Root, "_account").TwoFactorEnabled);
        Assert.Empty(page.Value<IReadOnlyList<string>>(factor, "_codes"));
    }

    [Fact]
    public async Task DownloadAcknowledgementDoesNotPretendCodesWereSaved()
    {
        await using var page = await Page.Open();
        var factor = await page.BeginFactor(TwoFactorMethod.App);
        await page.Set(factor, "_code", "123456"); await page.Call(factor, "SubmitAsync");
        page.Js.Module.DownloadWorks = false;
        await page.Call(factor, "DownloadCodesAsync");
        Assert.Contains("Copy the codes instead", page.Value<string>(factor, "_copyStatus"));
        page.Js.Module.DownloadWorks = true;
        await page.Call(factor, "DownloadCodesAsync");
        Assert.Contains("Download started", page.Value<string>(factor, "_copyStatus"));
        Assert.False(page.Value<bool>(factor, "_saved"));
        // Finishing before the box is ticked says so, rather than a greyed-out button.
        await page.Call(factor, "SubmitAsync");
        Assert.Contains("Tick the box", page.Value<string?>(factor, "_error"));
    }

    [Theory]
    [InlineData("Current password is incorrect.")]
    [InlineData("Your current password isn't right.")]
    public async Task KnownWrongCurrentPasswordErrorsStayByTheirField(string message)
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_current", "current123"); await page.Set(dialog, "_new", "New-password123"); await page.Set(dialog, "_confirm", "New-password123");
        page.Http.Message = message;
        await page.Call(dialog, "ChangeAsync");
        Assert.Equal(message, page.Value<string>(dialog, "_currentError"));
        Assert.Null(page.Value<string?>(dialog, "_error"));
        Assert.Equal("security-current-password", page.ActiveContentModal.InitialFocusId);
        await page.Call(dialog, "SetConfirmation", "New-password123");
        Assert.Equal(message, page.Value<string>(dialog, "_currentError"));
    }

    [Fact]
    public async Task ForgotPasswordEmailsTheSignedInAddressAndStaysInTheDialog()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_current", "half-remembered");
        await page.Call(dialog, "SendResetAsync");
        Assert.Equal("controlled@example.com", Assert.Single(page.Resets.Sent));
        Assert.Contains("Check your email", await page.Html());
        Assert.Contains("We sent a link to controlled@example.com", await page.Html());
        Assert.Equal("", page.Value<string>(dialog, "_current"));
        Assert.Null(page.Navigation.Target);
        await page.Call(dialog, "SendResetAsync");
        Assert.Equal(2, page.Resets.Sent.Count);
        Assert.Contains("We sent you another reset link.", await page.Html());
        Assert.DoesNotContain("replaces", await page.Html()); // The existing endpoint does not report link-replacement semantics.
        await page.Call(dialog, "ConfirmAsync");
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen"));
    }

    [Fact]
    public async Task NewPasswordUsesTheSameMeterAndRulesAsSignUp()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_new", "a-long-password-that-is-unique");
        Assert.Contains("password-strength", await page.Html());
        Assert.DoesNotContain("8 to 128", await page.Html());
        await page.Set(dialog, "_current", "current123"); await page.Set(dialog, "_confirm", "a-long-password-that-is-unique");
        await page.Call(dialog, "ChangeAsync");
        Assert.Equal("Choose a password that meets all three requirements.", page.Value<string?>(dialog, "_newError"));
        Assert.Empty(page.Http.Paths);
        Assert.True(PasswordRules.MeetsAll("ExistingSignUp12"));
        Assert.False(PasswordRules.MeetsAll("onlylowercase"));
        Assert.True(new PasswordInput().ShowStrength);
    }

    [Fact]
    public async Task LongSessionListsAreCollapsedButKeepTheCurrentSessionFirst()
    {
        var api = new SecurityApi();
        api.Info = api.Info with { Sessions = api.Info.Sessions.Concat(Enumerable.Range(3, 7).Select(i => new AccountSession($"session-{i}", $"Browser{i}", "Computer", false, false, DateTimeOffset.UtcNow.AddHours(-i)))).ToArray() };
        await using var page = await Page.Open(api);
        Assert.Equal(5, (await page.Html()).Split("security__line--device").Length - 1);
        Assert.Contains("Show all 9 sessions", await page.Html());
        Assert.Contains("This device", await page.Html());
        await page.Set(page.Root, "_showAllSessions", true);
        Assert.Equal(9, (await page.Html()).Split("security__line--device").Length - 1);
    }

    [Fact]
    public void RelativeSessionTimesRetainTimezoneAwareAbsoluteFallback()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", SecurityText.SessionWhen(now.AddSeconds(-20), now, TimeZoneInfo.Utc));
        Assert.Equal("20 min ago", SecurityText.SessionWhen(now.AddMinutes(-20), now, TimeZoneInfo.Utc));
        Assert.Equal("2 hr ago", SecurityText.SessionWhen(now.AddHours(-2), now, TimeZoneInfo.Utc));
        Assert.Equal("3 Nov, 12:00 PM", SecurityText.SessionWhen(now.AddDays(-2), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task NonFormDialogsKeepTheirOriginalButtonBehaviour()
    {
        await using var page = await Page.Open();
        await page.Call(page.Root, "AskToRevoke", page.Api.Info.Sessions.Single(session => !session.Current));
        Assert.False(page.ActiveDialog.SubmitOnEnter);
        Assert.DoesNotContain("<form", await page.Html());
        Assert.DoesNotContain("type=\"submit\"", await page.Html());
    }

    [Fact]
    public async Task PasswordUsesContentModalAndNativeFormWithAssociatedFooter()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var modal = page.ActiveContentModal;
        Assert.Equal("Change password", modal.Title);
        Assert.True(modal.OnSubmit.HasDelegate);
        Assert.False(modal.CloseOnBackdrop);
        Assert.Equal("security-current-password", modal.InitialFocusId);
        var html = await page.Html();
        Assert.Contains("content-modal__body", html);
        Assert.DoesNotContain("dialog-backdrop", html);
        var formId = System.Text.RegularExpressions.Regex.Match(html, "<form id=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(formId);
        Assert.Contains($"form=\"{formId}\"", html);
        Assert.Contains("Forgot your password?", html);
    }

    [Fact]
    public async Task ResetLinkSendingBlocksClosingAndConcurrentPasswordChanges()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Set(dialog, "_current", "current123");
        await page.Set(dialog, "_new", "New-password123");
        await page.Set(dialog, "_confirm", "New-password123");
        await page.Set(dialog, "_sending", true);
        Assert.True(page.ActiveContentModal.Busy);
        await page.Call(dialog, "CloseAsync");
        Assert.True(page.Value<bool>(page.Root, "_passwordOpen"));
        Assert.Equal("current123", page.Value<string>(dialog, "_current"));
        await page.Call(dialog, "ChangeAsync");
        Assert.Empty(page.Http.Paths);
        await page.Set(dialog, "_sending", false);
        await page.Call(dialog, "CloseAsync");
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen"));
        Assert.Equal("", page.Value<string>(dialog, "_current"));
        Assert.Equal("", page.Value<string>(dialog, "_new"));
        Assert.Equal("", page.Value<string>(dialog, "_confirm"));
    }

    [Fact]
    public async Task SentResetLinkUsesTheSameModalAndFocusesItsDoneAction()
    {
        await using var page = await Page.Open();
        await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Call(dialog, "SendResetAsync");
        Assert.Equal("Check your email", page.ActiveContentModal.Title);
        Assert.Equal("password-reset-done", page.ActiveContentModal.InitialFocusId);
        var html = await page.Html();
        Assert.Contains("id=\"password-reset-done\"", html);
        Assert.Contains("You can send it again in", html);
        Assert.DoesNotContain("security-current-password", html);
        Assert.DoesNotContain("dialog-backdrop", html);
        await page.Call(dialog, "ConfirmAsync");
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen"));
    }

    [Fact]
    public async Task MismatchStaysWithConfirmationEvenWhenNewAndCurrentMatch()
    {
        await using var page = await Page.Open(); await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>();
        await page.Call(dialog, "SetCurrent", "CurrentPass12"); await page.Call(dialog, "SetNew", "CurrentPass12");
        await page.Call(dialog, "SetConfirmation", "DifferentPass12"); await page.Call(dialog, "ChangeAsync");
        Assert.Null(page.Value<string?>(dialog, "_newError"));
        Assert.Equal("The new passwords don't match.", page.Value<string?>(dialog, "_confirmError"));
        Assert.Equal("security-confirm-password", page.ActiveContentModal.InitialFocusId);
        await page.Call(dialog, "SetConfirmation", "CurrentPass12"); Assert.Null(page.Value<string?>(dialog, "_confirmError"));
        await page.Call(dialog, "ChangeAsync"); Assert.Equal("Choose a different password.", page.Value<string?>(dialog, "_newError"));
        await page.Call(dialog, "SetCurrent", "AnotherCurrent12"); Assert.Null(page.Value<string?>(dialog, "_newError"));
        Assert.Empty(page.Http.Paths);
    }

    [Fact]
    public async Task AllPasswordFieldsAreLockedDuringSendingAndUseMediumFeedback()
    {
        await using var page = await Page.Open(); await page.Set(page.Root, "_passwordOpen", true);
        var dialog = page.Instance<ChangePasswordDialog>(); await page.Set(dialog, "_sending", true);
        var html = await page.Html();
        foreach (var id in new[] { "security-current-password", "security-new-password", "security-confirm-password" })
            Assert.Contains("disabled", System.Text.RegularExpressions.Regex.Match(html, $"<input[^>]*id=\"{id}\"[^>]*>").Value);
        var controls = page.PasswordInputs;
        Assert.Equal(3, controls.Count); Assert.All(controls, input => Assert.Equal(TextStyle.Small12Medium, input.HelperTextStyle));
        await page.Call(dialog, "SetNew", "Unexpected1"); Assert.Equal("", page.Value<string>(dialog, "_new"));
    }

    [Fact]
    public async Task AReturnedEmailLinkOpensSettingsRecoveryAndUsesTheExistingResetContract()
    {
        const string token = "controlled+/token== with space";
        await using var page = await Page.Open(link: new("controlled", token));
        var dialog = page.Instance<ChangePasswordDialog>(); var flow = page.Value<PasswordResetFlow>(dialog, "_reset");
        Assert.Equal("Choose a new password", page.ActiveContentModal.Title);
        Assert.DoesNotContain("security-current-password", await page.Html()); Assert.Null(page.Navigation.Target);
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); await page.Call(dialog, "ResetPasswordAsync");
        var request = Assert.Single(page.PasswordResets.Sent); Assert.Equal(token, request.Token); Assert.Equal("controlled", request.UserId);
        Assert.Empty(page.Http.Paths); Assert.Equal("Password updated", page.ActiveContentModal.Title);
        Assert.Equal("", flow.Password); Assert.Equal("", flow.Confirmation);
        Assert.Equal(PasswordResetSignOut.CompletePath, page.Navigation.Target); Assert.Null(page.Auth.CurrentUser);
        Assert.Contains("localStorage.removeItem", page.Js.Calls);
    }

    [Fact]
    public async Task FailedSessionCleanupRetriesSignOutNotTheAlreadyUsedResetToken()
    {
        await using var page = await Page.Open(link: new("controlled", "controlled-token"));
        var dialog = page.Instance<ChangePasswordDialog>(); var flow = page.Value<PasswordResetFlow>(dialog, "_reset");
        page.Js.FailRemoval = true;
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); await page.Call(dialog, "ResetPasswordAsync");
        Assert.True(flow.Done); Assert.Null(page.Navigation.Target); Assert.NotNull(page.Auth.CurrentUser);
        Assert.Contains("couldn't clear this browser", System.Net.WebUtility.HtmlDecode(await page.Html())); Assert.Contains("Sign out and continue", await page.Html());
        page.Js.FailRemoval = false; await page.Call(dialog, "ConfirmAsync");
        Assert.Single(page.PasswordResets.Sent); Assert.Equal(PasswordResetSignOut.CompletePath, page.Navigation.Target); Assert.Null(page.Auth.CurrentUser);
    }

    [Fact]
    public async Task AnExpiredEmailLinkCanRequestANewOneWithoutLeavingSettings()
    {
        await using var page = await Page.Open(link: new("controlled", "controlled-token"));
        var dialog = page.Instance<ChangePasswordDialog>(); var flow = page.Value<PasswordResetFlow>(dialog, "_reset");
        page.PasswordResets.Failure = new ApiException(HttpStatusCode.BadRequest, "Expired token", "InvalidToken", []);
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); await page.Call(dialog, "ResetPasswordAsync");
        Assert.True(flow.LinkSpent); Assert.Contains("Send a new link", await page.Html());
        Assert.DoesNotContain("security-reset-new-password", await page.Html());
        await page.Call(dialog, "ConfirmAsync"); Assert.Equal("controlled@example.com", Assert.Single(page.Resets.Sent));
        Assert.Equal("Check your email", page.ActiveContentModal.Title); Assert.Null(page.Navigation.Target);
    }

    [Fact]
    public async Task ALinkForAnotherAccountDoesNotOpenAResetForTheCurrentAccount()
    {
        await using var page = await Page.Open(link: new("another-user", "controlled-token"));
        Assert.False(page.Value<bool>(page.Root, "_passwordOpen")); Assert.Empty(page.PasswordResets.Sent);
    }

    private sealed class Page : IAsyncDisposable
    {
        public SecurityApi Api = null!;
        public PasswordHttp Http { get; } = new();
        public TestJs Js { get; } = new();
        public ResetLinks Resets { get; } = new();
        public ResetPasswords PasswordResets { get; } = new();
        public TestNavigation Navigation { get; } = new();
        public AuthState Auth { get; } = new();
        private readonly Capture _capture = new();
        private ServiceProvider _services = null!;
        private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        public SecuritySection Root => Instance<SecuritySection>();
        public ContentModal ActiveContentModal => _capture.Components.OfType<ContentModal>().Last(modal => modal.Open);
        public Dialog ActiveDialog => _capture.Components.OfType<Dialog>().Last(dialog => dialog.Open);
        public T Instance<T>() => (T)_capture.Components.Last(component => component is T);
        public IReadOnlyList<PasswordInput> PasswordInputs => _capture.Components.OfType<PasswordInput>().ToArray();
        public static async Task<Page> Open(IAccountSecurity? security = null, PasswordResetLink? link = null)
        {
            var page = new Page { Api = security as SecurityApi ?? new() };
            var auth = page.Auth; auth.SetUser(new AuthResponse { UserId = "controlled", Token = "controlled", Email = "controlled@example.com", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            var handoff = new PasswordResetHandoff(); if (link is not null) handoff.Stage(link.UserId, link.Token);
            page._services = new ServiceCollection().AddLogging().AddSingleton(auth).AddSingleton<IJSRuntime>(page.Js)
                .AddSingleton(new ProtectedLocalStorage(page.Js, new EphemeralDataProtectionProvider())).AddSingleton<TokenStorageService>()
                .AddSingleton<IAccountSecurity>(security ?? page.Api)
                .AddSingleton<NavigationManager>(page.Navigation).AddSingleton<IForgotPasswordService>(page.Resets).AddSingleton<IComponentActivator>(page._capture)
                .AddSingleton<IResetPasswordService>(page.PasswordResets).AddSingleton<IPasswordResetPreview, UnavailablePasswordResetPreview>().AddSingleton(handoff)
                .AddSingleton<PasswordResetSignOut>()
                .AddSingleton<AuroraInterop>().AddSingleton<UserClock>().AddSingleton<ToastService>()
                .AddSingleton(new HttpClient(page.Http) { BaseAddress = new Uri("http://localhost/") }).AddSingleton<ProfileClient>().BuildServiceProvider();
            page._renderer = new(page._services, page._services.GetRequiredService<ILoggerFactory>());
            page._root = await page._renderer.Dispatcher.InvokeAsync(() => page._renderer.RenderComponentAsync<SecuritySection>());
            return page;
        }
        public async Task<TwoFactorDialog> BeginFactor(TwoFactorMethod method = TwoFactorMethod.Email)
        {
            await Call(Root, "OpenTwoFactor"); var dialog = Instance<TwoFactorDialog>();
            await Call(dialog, "PickMethod", method);
            await Set(dialog, "_password", "current123"); await Call(dialog, "SubmitAsync"); return dialog;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public T Value<T>(object component, string name) => (T)(component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component)
            ?? component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component))!;
        private static void Render(object component) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        public Task Set(object component, string name, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
        { component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value); Render(component); });
        public Task Call(object component, string name, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        { var result = component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, args); if (result is Task task) await task; Render(component); });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type type) { var component = (IComponent)Activator.CreateInstance(type)!; Components.Add(component); return component; }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public string? Target;
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/security");
        protected override void NavigateToCore(string uri, bool forceLoad) => Target = uri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => Target = uri;
    }
    private sealed class ResetLinks : IForgotPasswordService
    {
        public List<string> Sent { get; } = [];
        public Task ForgotPasswordAsync(ForgotPasswordRequest request) { Sent.Add(request.Email); return Task.CompletedTask; }
    }
    private sealed class ResetPasswords : IResetPasswordService
    {
        public Exception? Failure;
        public List<ResetPasswordRequest> Sent { get; } = [];
        public Task ResetPasswordAsync(ResetPasswordRequest request) { Sent.Add(request); return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
    }
    private sealed class TestJs : IJSRuntime
    {
        public TestModule Module { get; } = new();
        public bool FailRemoval;
        public List<string> Calls { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args)
        {
            Calls.Add(id);
            if (id == "localStorage.removeItem" && FailRemoval) throw new JSException("Controlled storage failure");
            return ValueTask.FromResult(id == "import" ? (T)(object)Module : default!);
        }
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }
    private sealed class TestModule : IJSObjectReference
    {
        public bool CopyWorks, DownloadWorks;
        public List<string> Calls { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) { Calls.Add(id); return ValueTask.FromResult(id == "copyText" ? (T)(object)CopyWorks : id == "saveTextFile" ? (T)(object)DownloadWorks : default!); }
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PasswordHttp : HttpMessageHandler
    {
        public bool Fail;
        public string? Message;
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Paths.Add(request.RequestUri!.AbsolutePath); return Fail ? throw new HttpRequestException("Controlled failure") : Task.FromResult(Message is null ? new HttpResponseMessage(HttpStatusCode.NoContent) : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = Message }) }); }
    }
    private sealed class SecurityApi : IAccountSecurity
    {
        public bool Available => true;
        public bool FailRead, FailConfirm, FailDisable, FailRevoke;
        public int Cancels, Disables, Replacements, BulkRevokes, AppSetups, EmailSetups, Resends;
        public SecurityOperationException? CodeProblem;
        public AccountSecurityInfo Info = new(true, ["Password"], false, 8,
            [new("one", "Controlled browser", "Computer", false, true, DateTimeOffset.UtcNow), new("two", "Other browser", "Phone", true, false, DateTimeOffset.UtcNow.AddHours(-1))]);
        public Task<AccountSecurityInfo> ReadAsync(CancellationToken ct = default) => FailRead ? throw new HttpRequestException("Controlled failure") : Task.FromResult(Info);
        public Task<AuthenticatorSetup> BeginSetupAsync(string password, CancellationToken ct = default)
        { AppSetups++; return Task.FromResult(new AuthenticatorSetup("setup", "CONTROLLED-KEY", "otpauth://totp/controlled?secret=JBSWY3DPEHPK3PXP&issuer=controlled", DateTimeOffset.UtcNow.AddMinutes(5))); }
        public Task<EmailCodeSetup> BeginEmailSetupAsync(string password, CancellationToken ct = default)
        { EmailSetups++; return Task.FromResult(new EmailCodeSetup("email-setup", "controlled@example.com", DateTimeOffset.UtcNow.AddMinutes(10))); }
        public Task<EmailCodeSetup> ResendEmailCodeAsync(string id, CancellationToken ct = default)
        { Resends++; return Task.FromResult(new EmailCodeSetup(id, "controlled@example.com", DateTimeOffset.UtcNow.AddMinutes(10))); }
        public Task<TwoFactorResult> ConfirmEmailSetupAsync(string id, string code, CancellationToken ct = default)
        {
            if (FailConfirm) throw new HttpRequestException("Controlled failure"); if (CodeProblem is not null) throw CodeProblem;
            var first = !Info.TwoFactorEnabled;
            Info = Info with { TwoFactorEnabled = true, Methods = [.. Info.Methods ?? [], TwoFactorMethod.Email] };
            return Task.FromResult(new TwoFactorResult(Info, first ? ["EEEEEE-FFFFFF"] : []));
        }
        public Task<TwoFactorResult> ConfirmSetupAsync(string id, string code, CancellationToken ct = default)
        {
            if (FailConfirm) throw new HttpRequestException("Controlled failure"); if (CodeProblem is not null) throw CodeProblem;
            var first = !Info.TwoFactorEnabled;
            Info = Info with { TwoFactorEnabled = true, Methods = [.. Info.Methods ?? [], TwoFactorMethod.App] };
            return Task.FromResult(new TwoFactorResult(Info, first ? ["AAAAAA-BBBBBB", "CCCCCC-DDDDDD"] : []));
        }
        public Task CancelSetupAsync(string id, CancellationToken ct = default) { Cancels++; return Task.CompletedTask; }
        public Task<AccountSecurityInfo> DisableAsync(string password, string code, CancellationToken ct = default)
        { Disables++; if (FailDisable) throw new SecurityOperationException("Wrong code.", SecurityField.Code); Info = Info with { TwoFactorEnabled = false, Methods = null }; return Task.FromResult(Info); }
        public int Removals;
        public Task<AccountSecurityInfo> RemoveMethodAsync(string password, TwoFactorMethod method, CancellationToken ct = default)
        { Removals++; Info = Info with { Methods = [.. (Info.Methods ?? []).Where(way => way != method)] }; return Task.FromResult(Info); }
        public Task<TwoFactorResult> ReplaceRecoveryCodesAsync(string password, string code, CancellationToken ct = default)
        { Replacements++; return Task.FromResult(new TwoFactorResult(Info, ["NEWAAA-NEWBBB"])); }
        public Task RevokeSessionAsync(string id, CancellationToken ct = default) => FailRevoke ? throw new HttpRequestException("Controlled failure") : Task.CompletedTask;
        public Task RevokeOtherSessionsAsync(CancellationToken ct = default) { BulkRevokes++; return Task.CompletedTask; }
    }
}
