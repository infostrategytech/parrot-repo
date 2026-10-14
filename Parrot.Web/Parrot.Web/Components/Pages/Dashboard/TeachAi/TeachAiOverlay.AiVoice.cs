using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

/// <summary>
/// Talking to the AI itself (voice orientation on): Talk gets a signed ElevenLabs link from
/// Parrot's API and the browser connects to it. ElevenLabs hears the owner and speaks the AI's
/// answers; both sides' words land in the thread as they are said. The panel's voice bar, orb,
/// mute and the light round the panel work as they do for the scripted conversation.
/// </summary>
public partial class TeachAiOverlay
{
    [Inject] private OrientationApi Orientation { get; set; } = default!;
    [Inject] private AiVoiceInterop AiVoiceJs { get; set; } = default!;

    private bool AiVoice => Knowledge is OrientationBusinessKnowledge;

    private AiConversation? _aiCall;
    private bool _aiStarting;
    // Bumped whenever a call ends or is abandoned, so one that finishes connecting after the
    // owner ended it, closed the panel or left the page is ended at once and never shown.
    private int _aiGeneration;

    // Starting or in a call: what the call reports still counts (the greeting can arrive before it has finished connecting).
    private bool AiActive => _aiStarting || _aiCall is not null;

    private async Task StartAiVoiceAsync()
    {
        if (_conversation is null || _aiCall is not null || _aiStarting) return;
        _aiStarting = true;
        var generation = ++_aiGeneration;
        _voiceNotice = null;
        _muted = false;
        if (_conversation.Step == TeachAiStep.Welcome) _conversation.StartLive();
        _mode = Mode.Voice;
        _phase = Phase.Thinking;
        // The orb has to be on screen before its level can be bound.
        _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StateHasChanged();
        await _rendered.Task;
        try
        {
            var session = await Orientation.StartVoiceAsync();
            if (generation != _aiGeneration) return;
            IReadOnlyList<ElementReference> levels = _orb is null ? [] : LevelTargets;
            var call = await AiVoiceJs.ConverseAsync(session, levels, _self!,
                nameof(AiSaid), nameof(AiModeChanged), nameof(AiEnded), nameof(AiFailed), nameof(AiAlive));
            if (generation != _aiGeneration)
            {
                await call.EndAsync();
                return;
            }
            _aiCall = call;
            _phase = Phase.Listening;
        }
        catch (ApiException ex)
        {
            BackToStart(ex.UserMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JSException)
        {
            BackToStart("We couldn't start the voice chat. Check your connection and try again.");
        }
        finally
        {
            _aiStarting = false;
            StateHasChanged();
        }
    }

    private void BackToStart(string notice)
    {
        _mode = Mode.Text;
        _phase = Phase.Idle;
        _voiceNotice = notice;
    }

    [JSInvokable]
    public Task AiSaid(string who, string text) => InvokeAsync(() =>
    {
        if (!AiActive || _conversation is null || string.IsNullOrWhiteSpace(text)) return;
        _conversation.Lines.Add(new TeachAiLine(who == "user" ? TeachAiSpeaker.Person : TeachAiSpeaker.Agent, text.Trim()));
        _scrollPending = true;
        StateHasChanged();
    });

    // Asked by the browser every few seconds: false once the call has been ended or abandoned
    // here, so a call the browser never heard End for still closes.
    [JSInvokable]
    public bool AiAlive() => AiActive && _wasOpen;

    [JSInvokable]
    public Task AiModeChanged(string mode) => InvokeAsync(() =>
    {
        if (!AiActive) return;
        _phase = mode == "speaking" ? Phase.Speaking : Phase.Listening;
        StateHasChanged();
    });

    // Ended from the other side (the AI finished, the link expired, the connection dropped).
    [JSInvokable]
    public Task AiEnded(string reason) => InvokeAsync(() =>
    {
        if (!AiActive) return;
        _aiGeneration++;
        _aiCall = null;
        _muted = false;
        _pausedAway = false;
        _mode = Mode.Text;
        _phase = Phase.Idle;
        _voiceNotice ??= "The voice chat ended. Press Talk to carry on.";
        _scrollPending = true;
        StateHasChanged();
    });

    [JSInvokable]
    public Task AiFailed(string message) => InvokeAsync(() =>
    {
        if (!AiActive) return;
        _voiceNotice = "Something went wrong with the voice chat. Press Talk to try again.";
        StateHasChanged();
    });

    private async Task ToggleAiMuteAsync()
    {
        _muted = !_muted;
        if (_aiCall is not null) await _aiCall.SetMutedAsync(_muted || _pausedAway);
    }

    // Nobody is listened to without knowing: the AI hears nothing while the tab is hidden.
    private async Task AiVisibilityChangedAsync(bool visible)
    {
        _pausedAway = !visible;
        if (_aiCall is not null) await _aiCall.SetMutedAsync(_muted || _pausedAway);
        StateHasChanged();
    }

    private async Task EndAiVoiceAsync()
    {
        _aiGeneration++;
        var call = _aiCall;
        _aiCall = null;
        if (call is not null) await call.EndAsync();
    }
}
