using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Features.DynamicForms;

/// <summary>Which form to ask for: the flow it belongs to ("agent-setup", "knowledge") and what it's about (an agent's or topic's id).</summary>
public sealed record DynamicFormRequest(string Flow, string? SubjectId = null);

/// <summary>
/// What comes next: the next form, or none when the flow is done; a confirmation to show, if any ("Saved to your job
/// description."); and what the reader changed or left out of the form, to log for the team.
/// </summary>
public sealed record DynamicFormStep(DynamicForm? Form, string? Message = null, IReadOnlyList<string>? Problems = null);

/// <summary>The form couldn't be read (nothing in it could be shown). Problems lists why, for the team.</summary>
public sealed class DynamicFormException(IReadOnlyList<string> problems)
    : Exception("The form couldn't be shown: " + string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Where generated forms come from and where their answers go (designer, 27 October 2026): the one place the
/// backend plugs in. The screens only ever ask for the first form of a flow, and send the answers to get the
/// next. Registered in Program.cs: <see cref="UnavailableDynamicFormSource"/> until the backend's endpoints
/// exist, then <see cref="ApiDynamicFormSource"/> (dynamic-form-backend-requests).
/// </summary>
public interface IDynamicFormSource
{
    /// <summary>Whether generated forms can be had at all; the screens say so plainly when they can't.</summary>
    bool Available { get; }

    /// <summary>The first form of a flow.</summary>
    Task<DynamicFormStep> StartAsync(DynamicFormRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends the answers (a FormResult, as <see cref="DynamicFormWriter"/> packs it) and gets what comes next.</summary>
    Task<DynamicFormStep> SendAsync(DynamicFormRequest request, string result, CancellationToken cancellationToken = default);
}

/// <summary>Until the backend's endpoints exist: there are no generated forms, said plainly.</summary>
public sealed class UnavailableDynamicFormSource : IDynamicFormSource
{
    public bool Available => false;

    public Task<DynamicFormStep> StartAsync(DynamicFormRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Generated forms aren't available yet.");

    public Task<DynamicFormStep> SendAsync(DynamicFormRequest request, string result, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Generated forms aren't available yet.");
}

/// <summary>
/// The backend's endpoints, as proposed in dynamic-form-backend-requests (change the two paths here if they
/// differ). Both take { flow, subjectId } and answer with the next step: { form, message } where form is a Form
/// (dynamic-form-schema.json) or null when the flow is done. A bare Form is read too. Sending also carries
/// { result }, the FormResult.
/// </summary>
public sealed class ApiDynamicFormSource(HttpClient http, AuthState auth) : IDynamicFormSource
{
    public const string StartPath = "api/forms/start";
    public const string SendPath = "api/forms/answer";

    public bool Available => true;

    public Task<DynamicFormStep> StartAsync(DynamicFormRequest request, CancellationToken cancellationToken = default) =>
        PostAsync(StartPath, new Dictionary<string, object?> { ["flow"] = request.Flow, ["subjectId"] = request.SubjectId }, cancellationToken);

    public Task<DynamicFormStep> SendAsync(DynamicFormRequest request, string result, CancellationToken cancellationToken = default)
    {
        using var packed = JsonDocument.Parse(result);
        return PostAsync(SendPath, new Dictionary<string, object?>
        {
            ["flow"] = request.Flow,
            ["subjectId"] = request.SubjectId,
            ["result"] = packed.RootElement.Clone(),
        }, cancellationToken);
    }

    private async Task<DynamicFormStep> PostAsync(string path, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to continue.");
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The form couldn't be fetched or saved.", null, response.StatusCode);
        return Read(text);
    }

    /// <summary>The next step, from the response: { form, message }, or a bare Form.</summary>
    public static DynamicFormStep Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new DynamicFormStep(null);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DynamicFormException(["The response isn't an object."]);
        var said = root.TryGetProperty("message", out var line) && line.ValueKind == JsonValueKind.String ? line.GetString() : null;
        var formElement = root.TryGetProperty("form", out var inner) ? inner : root.TryGetProperty("questions", out _) ? root : default;
        if (formElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new DynamicFormStep(null, said);
        var read = DynamicFormReader.Read(formElement);
        return read.Form is { } form ? new DynamicFormStep(form, said, read.Problems) : throw new DynamicFormException(read.Problems);
    }
}
