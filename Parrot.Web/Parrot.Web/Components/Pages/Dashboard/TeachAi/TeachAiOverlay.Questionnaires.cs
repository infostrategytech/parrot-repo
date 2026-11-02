using System.Net;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

/// <summary>
/// The forms the AI sends while it learns an agent's job (talking to the AI only). While the panel
/// is open for an agent, the AI service's form stream is read; each form joins the end of the
/// conversation as a card, answered where it is. The stream drops when the token expires or the
/// connection goes, so it is opened again after a pause; a form it repeats is shown once.
/// </summary>
public partial class TeachAiOverlay
{
    private static readonly TimeSpan FormsRetryFirst = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FormsRetryMost = TimeSpan.FromSeconds(30);

    private sealed record ArrivedForm(OrientationForm Delivery, OrientationQuestionnaire Questionnaire);

    // In arrival order; a sent card stays, so the thread keeps what was answered.
    private readonly List<ArrivedForm> _forms = [];
    private CancellationTokenSource? _formsListening;
    private string? _formsAgent;

    private void ListenForForms()
    {
        if (!AiVoice || State.AgentId is not { } agentId || _formsListening is not null) return;
        // Another agent's forms are not this one's.
        if (_formsAgent != agentId) _forms.Clear();
        _formsAgent = agentId;
        _formsListening = new CancellationTokenSource();
        _ = ReadFormsAsync(agentId, _formsListening.Token);
    }

    private void StopListeningForForms()
    {
        // The panel closed: an answer still being typed back is no longer wanted either.
        StopTypingToAi();
        _formsListening?.Cancel();
        _formsListening?.Dispose();
        _formsListening = null;
    }

    private async Task ReadFormsAsync(string agentId, CancellationToken cancellationToken)
    {
        var wait = FormsRetryFirst;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var form in Orientation.ReadFormsAsync(agentId, cancellationToken))
                {
                    wait = FormsRetryFirst;
                    await InvokeAsync(() => Arrived(form));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Signed out, no longer allowed, or the AI does not know this agent: asking again changes nothing.
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException or IOException)
            {
                // The connection dropped or the service is busy: try again after a pause.
            }

            try { await Task.Delay(wait, cancellationToken); }
            catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, FormsRetryMost.Ticks));
        }
    }

    private void Arrived(OrientationForm form)
    {
        if (_forms.Any(f => f.Delivery.EventId == form.EventId)) return;
        if (OrientationQuestionnaire.Read(form.Form) is not { } questionnaire) return;
        _forms.Add(new ArrivedForm(form, questionnaire));
        // A form can come before anything is said; it is answered in the conversation, so open it.
        if (_conversation is { Step: TeachAiStep.Welcome }) _conversation.StartLive();
        _scrollPending = true;
        StateHasChanged();
    }

    private Func<IReadOnlyDictionary<string, object?>, Task<string?>> SendAnswers(ArrivedForm form) => async answers =>
    {
        if (_formsAgent is not { } agentId) return "Open Teach more from the agent again to send this.";
        try
        {
            await Orientation.AnswerFormAsync(agentId, form.Delivery, answers);
            return null;
        }
        catch (ApiException ex)
        {
            return ex.UserMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "We couldn't send your answers. Check your connection and try again.";
        }
    };
}
