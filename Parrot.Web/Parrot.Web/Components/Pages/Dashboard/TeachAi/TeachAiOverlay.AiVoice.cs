using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
        // The AI learns about one agent's job at a time; opened from no agent, there is no one to teach.
        if (State.AgentId is not { } agentId)
        {
            BackToStart("Open Teach more from an agent to talk to the AI about its job.");
            return;
        }
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
            var session = await Orientation.StartVoiceAsync(agentId);
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

    // ── Typing to the AI ─────────────────────────────────────────────────────

    private bool _aiTyping;
    private CancellationTokenSource? _aiTypingCancel;

    private bool CanSendToAi => _text.Trim().Length > 0 && !_aiTyping && !AiActive;

    private async Task AiComposerKeyAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !args.ShiftKey && !args.IsComposing) await SendToAiAsync();
    }

    // The owner's words join the thread at once; the AI's answer arrives in pieces and its line
    // grows as they come (Omni-AI's /orientation/text), the thinking dots standing in until then.
    private async Task SendToAiAsync()
    {
        if (_conversation is null || !CanSendToAi) return;
        if (State.AgentId is not { } agentId)
        {
            BackToStart("Open Teach more from an agent to talk to the AI about its job.");
            return;
        }
        var text = _text.Trim();
        _text = "";
        _voiceNotice = null;
        if (_conversation.Step == TeachAiStep.Welcome) _conversation.StartLive();
        _conversation.Lines.Add(new TeachAiLine(TeachAiSpeaker.Person, text));
        _aiTyping = true;
        _phase = Phase.Thinking;
        _scrollPending = true;
        _aiTypingCancel = new CancellationTokenSource();
        StateHasChanged();

        var reply = new StringBuilder();
        int? at = null;
        try
        {
            await foreach (var piece in Orientation.SendTextAsync(agentId, text, _aiTypingCancel.Token))
            {
                reply.Append(piece);
                var line = new TeachAiLine(TeachAiSpeaker.Agent, reply.ToString().Trim());
                if (at is { } index)
                {
                    _conversation.Lines[index] = line;
                }
                else
                {
                    _conversation.Lines.Add(line);
                    at = _conversation.Lines.Count - 1;
                    _phase = Phase.Idle;
                }
                _scrollPending = true;
                StateHasChanged();
            }
            if (at is null) _voiceNotice = "The AI didn't answer. Try again.";
        }
        catch (OperationCanceledException) when (_aiTypingCancel?.IsCancellationRequested == true)
        {
            // The panel closed while the AI was answering.
        }
        catch (ApiException ex)
        {
            _voiceNotice = ex.UserMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _voiceNotice = "We couldn't reach the AI. Check your connection and try again.";
        }
        finally
        {
            _aiTyping = false;
            if (_phase == Phase.Thinking) _phase = Phase.Idle;
            _aiTypingCancel?.Dispose();
            _aiTypingCancel = null;
            _focusComposer = true;
            StateHasChanged();
        }
    }

    private void StopTypingToAi() => _aiTypingCancel?.Cancel();

    // From the start screen: open the conversation with the box ready, nothing sent yet.
    private Task TypeToAi()
    {
        _conversation?.StartLive();
        _mode = Mode.Text;
        _focusComposer = true;
        return Task.CompletedTask;
    }

    private async Task EndAiVoiceAsync()
    {
        _aiGeneration++;
        var call = _aiCall;
        _aiCall = null;
        if (call is not null) await call.EndAsync();
    }
}
