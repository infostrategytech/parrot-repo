using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Features.Auth.ResetPassword.Models;
using Parrot.Web.Features.Auth.ResetPassword.Services;
using Parrot.Web.Features.Auth.ResetPassword.State;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// The production reset contract with controlled responses, not the local demo token or dataset.
public sealed class PasswordRecoveryTests
{
    [Theory]
    [InlineData("LongUniquePassphrase", "Fair")]
    [InlineData("long-unique-passphrase123", "Fair")]
    [InlineData("ValidPass1", "Good")]
    [InlineData("LongValidPass123", "Strong")]
    public async Task PositiveStrengthRequiresAllRequiredRules(string value, string expected)
    {
        await using var page = await RenderPage.Open<PasswordInput>(new() { ["Id"] = "controlled-password", ["FloatingLabel"] = "New password", ["IsNew"] = true, ["Value"] = value });
        Assert.Contains($"Password strength: {expected}", await page.Html());
    }

    [Fact]
    public async Task SharedFieldsUseActualValuesMediumErrorsAndOneMeter()
    {
        await using var page = await RenderPage.Open<NewPasswordFields>(new() { ["Password"] = "ValidPass1", ["Confirmation"] = "ValidPass1", ["PasswordError"] = "Controlled error" });
        var html = await page.Html();
        Assert.Equal(2, Regex.Matches(html, "value=\"ValidPass1\"").Count);
        Assert.Single(Regex.Matches(html, "class=\"password-strength ").Cast<Match>());
        Assert.Contains("Controlled error", html);
        Assert.All(page.Components.OfType<PasswordInput>(), input => Assert.Equal(TextStyle.Small12Medium, input.HelperTextStyle));
    }

    [Fact]
    public async Task SharedResendWaitsAfterSuccessAndCannotSubmitDuringTheCountdown()
    {
        var calls = 0;
        await using var page = await RenderPage.Open<ResendButton>(new() { ["SentRevision"] = 1, ["OnResend"] = EventCallback.Factory.Create(new object(), () => calls++) });
        var html = await page.Html(); Assert.Contains("You can send it again in", html); Assert.DoesNotContain("<button", html); // A calm line while it waits, no greyed button (designer, 8 November 2026).
        Assert.Equal(30, page.Component<ResendButton>().CooldownSeconds);
        await page.Call(page.Component<ResendButton>(), "ResendAsync"); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResendWithoutASuccessfulSendOrWithZeroWaitCanBeUsed()
    {
        var calls = 0;
        await using var page = await RenderPage.Open<ResendButton>(new() { ["SentRevision"] = 0, ["OnResend"] = EventCallback.Factory.Create(new object(), () => calls++) });
        await page.Call(page.Component<ResendButton>(), "ResendAsync"); Assert.Equal(1, calls);
        Assert.DoesNotContain("disabled", await page.Html());
    }

    [Theory]
    [InlineData(null, "controlled-token")]
    [InlineData("controlled-user", null)]
    [InlineData("", "")]
    public async Task MissingEmailLinkCannotBeReplacedByASignedInSession(string? user, string? token)
    {
        var api = new ResetApi(); var flow = new PasswordResetFlow(api, user, token);
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); await flow.SubmitAsync();
        Assert.True(flow.LinkSpent); Assert.False(flow.Done); Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task ResetSendsTheUnmodifiedEmailCredentialsAndClearsPasswordsAfterSuccess()
    {
        var api = new ResetApi(); const string token = "controlled+/token== with space";
        var flow = new PasswordResetFlow(api, "controlled-user", token);
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); await flow.SubmitAsync();
        var request = Assert.Single(api.Requests); Assert.Equal(token, request.Token); Assert.Equal("controlled-user", request.UserId);
        Assert.Equal("ValidPass1", request.NewPassword); Assert.Equal(request.NewPassword, request.ConfirmNewPassword);
        Assert.True(flow.Done); Assert.False(flow.Busy); Assert.Equal("", flow.Password); Assert.Equal("", flow.Confirmation);
    }

    [Fact]
    public async Task InvalidFieldsBlockResetAndTheirErrorsClearOnCorrection()
    {
        var api = new ResetApi(); var flow = new PasswordResetFlow(api, "controlled", "controlled-token");
        flow.SetPassword("weak"); await flow.SubmitAsync(); Assert.NotNull(flow.PasswordError); Assert.NotNull(flow.ConfirmationError);
        flow.SetPassword("ValidPass1"); Assert.Null(flow.PasswordError); Assert.Null(flow.ConfirmationError);
        flow.SetConfirmation("DifferentPass1"); await flow.SubmitAsync(); Assert.Null(flow.PasswordError); Assert.NotNull(flow.ConfirmationError);
        flow.SetConfirmation("ValidPass1"); Assert.Null(flow.ConfirmationError); Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task OverlongPasswordsAreRejectedWithoutChangingTheApiContract()
    {
        var api = new ResetApi(); var flow = new PasswordResetFlow(api, "controlled", "controlled-token");
        flow.SetPassword("Aa1" + new string('b', 126)); flow.SetConfirmation(flow.Password); await flow.SubmitAsync();
        Assert.NotNull(flow.PasswordError); Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AnExpiredOrUsedLinkClearsSecretsAndOffersAFreshRequestNotARetryWithTheSameToken()
    {
        var api = new ResetApi { Failure = new ApiException(HttpStatusCode.BadRequest, "Invalid token", "InvalidToken", []) };
        var flow = new PasswordResetFlow(api, "controlled", "controlled-token"); flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1");
        await flow.SubmitAsync(); Assert.True(flow.LinkSpent); Assert.Contains("expired or has already been used", flow.Error);
        Assert.Equal("", flow.Password); Assert.Equal("", flow.Confirmation);
        await flow.SubmitAsync(); Assert.Single(api.Requests);
    }

    [Fact]
    public async Task ConnectionFailureRetainsValuesAndAllowsRetry()
    {
        var api = new ResetApi { Failure = new HttpRequestException("Controlled offline") };
        var flow = new PasswordResetFlow(api, "controlled", "controlled-token"); flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1");
        await flow.SubmitAsync(); Assert.False(flow.Done); Assert.False(flow.Busy); Assert.Contains("internet connection", flow.Error);
        Assert.Equal("ValidPass1", flow.Password); api.Failure = null; await flow.SubmitAsync(); Assert.True(flow.Done);
    }

    [Fact]
    public async Task ServerPolicyErrorsRemainBesideThePasswordRatherThanBeingCalledAnExpiredLink()
    {
        var api = new ResetApi { Failure = new ApiException(HttpStatusCode.BadRequest, "Policy", "PasswordPolicy", ["Choose a different password."]) };
        var flow = new PasswordResetFlow(api, "controlled", "controlled-token"); flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1");
        await flow.SubmitAsync(); Assert.False(flow.LinkSpent); Assert.Equal("Choose a different password.", flow.PasswordError);
    }

    [Fact]
    public async Task PendingResetBlocksRepeatedSavesAndFieldChanges()
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ResetApi { Pause = pause.Task }; var flow = new PasswordResetFlow(api, "controlled", "controlled-token");
        flow.SetPassword("ValidPass1"); flow.SetConfirmation("ValidPass1"); var saving = flow.SubmitAsync();
        Assert.True(flow.Busy); await flow.SubmitAsync(); flow.SetPassword("Unexpected1"); Assert.Equal("ValidPass1", flow.Password);
        Assert.Single(api.Requests); pause.SetResult(); await saving; Assert.True(flow.Done);
    }

    [Fact]
    public void EmailLinkHandoffIsAccountMatchedAndConsumedOnlyOnce()
    {
        var handoff = new PasswordResetHandoff(); handoff.Stage("controlled-user", "controlled+/token==");
        Assert.Null(handoff.Take("another-user")); Assert.Null(handoff.Take("controlled-user"));
        handoff.Stage("controlled-user", "controlled+/token=="); var link = handoff.Take("controlled-user");
        Assert.Equal("controlled+/token==", link!.Token); Assert.Null(handoff.Take("controlled-user"));
        Assert.False(PasswordResetHandoff.IsSameAccount(null, "controlled-user"));
    }

    [Fact]
    public void RealModeHasNoSyntheticResetLink()
    { Assert.Null(new UnavailablePasswordResetPreview().LinkFor("controlled-user")); }

    [Theory]
    [InlineData("/reset-password", true)]
    [InlineData("/forgot-password/", true)]
    [InlineData("/reset-password/complete", true)]
    [InlineData("/reset-password/complete/untrusted", false)]
    [InlineData("/dashboard/settings/security", false)]
    [InlineData("/dashboard/inbox", false)]
    [InlineData("/reset-password/untrusted", false)]
    public void ExpiredSessionsOnlyKeepTheAlreadyPublicRecoveryEntriesOpen(string path, bool expected)
    { Assert.Equal(expected, PasswordResetHandoff.IsRecoveryPath(path)); }

    private sealed class ResetApi : IResetPasswordService
    {
        public Exception? Failure; public Task? Pause; public List<ResetPasswordRequest> Requests = [];
        public async Task ResetPasswordAsync(ResetPasswordRequest request) { Requests.Add(request); if (Pause is not null) await Pause; if (Failure is not null) throw Failure; }
    }

    private sealed class RenderPage : IAsyncDisposable
    {
        public List<IComponent> Components => _capture.Components;
        private Capture _capture = new(); private ServiceProvider _services = null!; private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        public T Component<T>() => Components.OfType<T>().Last();
        public static async Task<RenderPage> Open<T>(Dictionary<string, object?> parameters) where T : IComponent
        {
            var page = new RenderPage();
            page._services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(page._capture).AddSingleton<IJSRuntime>(new NoJs()).AddSingleton<AuroraInterop>().BuildServiceProvider();
            page._renderer = new(page._services, page._services.GetRequiredService<ILoggerFactory>());
            page._root = await page._renderer.Dispatcher.InvokeAsync(() => page._renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))); return page;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task Call(object component, string method) => _renderer.Dispatcher.InvokeAsync(async () => { if (component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null) is Task task) await task; });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator { public List<IComponent> Components = []; public IComponent CreateInstance(Type type) { var result = (IComponent)Activator.CreateInstance(type)!; Components.Add(result); return result; } }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }
}
