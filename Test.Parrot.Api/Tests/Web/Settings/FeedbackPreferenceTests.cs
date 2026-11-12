using Microsoft.JSInterop;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class FeedbackPreferenceTests
{
    [Theory]
    [InlineData("js")]
    [InlineData("disconnected")]
    [InlineData("unavailable")]
    [InlineData("cancelled")]
    public async Task BrowserFailuresNeverBecomeAConfirmedReadOrSave(string failure)
    {
        Exception error = failure switch { "js" => new JSException("Controlled failure"), "disconnected" => new JSDisconnectedException("Controlled disconnect"), "cancelled" => new TaskCanceledException(), _ => new InvalidOperationException("No browser") };
        var feedback = new FeedbackService(new ControlledJs(error: error));
        Assert.Null(await feedback.ReadEnabledAsync());
        var result = await feedback.TrySetEnabledAsync(true);
        Assert.False(result.Applied);
        Assert.False(result.Saved);
    }

    [Fact]
    public async Task SavedAndSessionOnlyChoicesAreDistinct()
    {
        var saved = new FeedbackPreferenceSaveResult(true, true, false);
        var temporary = new FeedbackPreferenceSaveResult(true, false, false);
        Assert.Equal(saved, await new FeedbackService(new ControlledJs(result: saved)).TrySetEnabledAsync(false));
        var sessionOnly = await new FeedbackService(new ControlledJs(result: temporary)).TrySetEnabledAsync(false);
        Assert.True(sessionOnly.Applied);
        Assert.False(sessionOnly.Saved);
        Assert.False(sessionOnly.Enabled);
    }

    [Fact]
    public async Task MissingAcknowledgementFromAnOlderScriptIsNotASavedConfirmation()
    {
        var result = await new FeedbackService(new ControlledJs()).TrySetEnabledAsync(true);
        Assert.False(result.Applied);
        Assert.False(result.Saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnownSoundStateIsKept(bool enabled) => Assert.Equal(enabled, await new FeedbackService(new ControlledJs(enabled: enabled)).ReadEnabledAsync());

    private sealed class ControlledJs(bool enabled = true, FeedbackPreferenceSaveResult? result = null, Exception? error = null) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (error is not null) throw error;
            object? value = identifier == "parrotFeedback.isEnabled" ? enabled : result;
            return ValueTask.FromResult((TValue)value!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
