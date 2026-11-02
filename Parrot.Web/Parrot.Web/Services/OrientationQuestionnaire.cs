using System.Text.Json;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Services;

/// <summary>One answer to pick, with what it means.</summary>
public sealed record QuestionOption(string Label, string? Description);

/// <summary>
/// One question in a form the AI sent. Type is the AI service's: one (pick one, each option
/// explained), select (pick one from a menu), many (pick any), yesno, text, consent (tick to agree).
/// A type this screen does not know is asked as text, so the owner can still answer it.
/// </summary>
public sealed record Question(
    string Id,
    string Type,
    string Text,
    string? Header,
    string? Placeholder,
    string? ConsentLabel,
    bool Required,
    string? Value,
    IReadOnlyList<QuestionOption> Options);

/// <summary>
/// A form the AI sent during orientation (OrientationForm.Form), read for showing: its title,
/// where it sits among the forms (step of steps), and its questions. AllowDecide offers to let
/// the AI choose instead of answering.
/// </summary>
public sealed record OrientationQuestionnaire(
    string Id,
    string Title,
    string? Subtitle,
    string SubmitLabel,
    bool AllowDecide,
    int? Step,
    int? Steps,
    IReadOnlyList<Question> Questions)
{
    public const string One = "one";
    public const string Select = "select";
    public const string Many = "many";
    public const string YesNo = "yesno";
    public const string Text = "text";
    public const string Consent = "consent";

    /// <summary>What is sent when the owner lets the AI decide; there are no answers of their own.</summary>
    public static readonly IReadOnlyDictionary<string, object?> AiDecides = new Dictionary<string, object?> { ["let_ai_decide"] = true };

    /// <summary>The form, or null when it has no questions this screen can show.</summary>
    public static OrientationQuestionnaire? Read(JsonElement form)
    {
        if (form.ValueKind != JsonValueKind.Object || !form.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        List<Question> questions = [];
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "id") is not { Length: > 0 } id) continue;
            var type = Str(item, "type")?.ToLowerInvariant() ?? Text;
            List<QuestionOption> options = [];
            if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in opts.EnumerateArray())
                {
                    // An option is { label, description } or just its label.
                    var label = option.ValueKind == JsonValueKind.String ? option.GetString() : Str(option, "label");
                    if (!string.IsNullOrWhiteSpace(label))
                        options.Add(new QuestionOption(label, option.ValueKind == JsonValueKind.Object ? Str(option, "description") : null));
                }
            }
            // Something to choose from needs choices; without them it is asked in words.
            if (type is One or Select or Many && options.Count == 0) type = Text;
            if (type is not (One or Select or Many or YesNo or Text or Consent)) type = Text;

            questions.Add(new Question(id, type, Str(item, "question") ?? Str(item, "header") ?? id, Str(item, "header"),
                Str(item, "placeholder"), Str(item, "consentLabel"), Bool(item, "required") ?? false, Raw(item, "value"), options));
        }

        if (questions.Count == 0) return null;
        return new OrientationQuestionnaire(
            Str(form, "id") ?? "",
            Str(form, "title") ?? "A few questions from your AI",
            Str(form, "subtitle"),
            Str(form, "submitLabel") ?? "Send answers",
            Bool(form, "allowDecide") ?? false,
            Int(form, "step"),
            Int(form, "steps"),
            questions);
    }

    /// <summary>The answers to start from: what the AI suggested for each question, if anything.</summary>
    public Dictionary<string, object?> Suggested()
    {
        Dictionary<string, object?> answers = [];
        foreach (var q in Questions)
        {
            answers[q.Id] = q.Type switch
            {
                YesNo or Consent => q.Value is null ? (q.Type == Consent ? false : null) : string.Equals(q.Value, "true", StringComparison.OrdinalIgnoreCase),
                Many => q.Value is null ? new List<string>() : q.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                One or Select => q.Options.Any(o => o.Label == q.Value) ? q.Value : null,
                _ => q.Value,
            };
        }
        return answers;
    }

    /// <summary>The ids of required questions still unanswered: no choice, no words, or consent not given.</summary>
    public IReadOnlyList<string> Missing(IReadOnlyDictionary<string, object?> answers) =>
        Questions.Where(q => q.Required && !Answered(q, answers.GetValueOrDefault(q.Id))).Select(q => q.Id).ToList();

    // ── As the designed question card (QuestionForm) shows it ──────────────────

    private const string Yes = "Yes";
    private const string No = "No";

    /// <summary>
    /// The questions as the designed card asks them: choices as tiles (with "Something else"
    /// for one-of and several), yes/no as two tiles, consent as one tile to tick, words as a box.
    /// </summary>
    public IReadOnlyList<FormQuestion> AsForm() => Questions.Select(q => q.Type switch
    {
        One or Select => new FormQuestion(q.Id, q.Text, FormQuestionKind.One, Options(q), q.Placeholder, AllowOther: true),
        Many => new FormQuestion(q.Id, q.Text, FormQuestionKind.Many, Options(q), q.Placeholder, AllowOther: true),
        YesNo => new FormQuestion(q.Id, q.Text, FormQuestionKind.One, [new(Yes), new(No)]),
        Consent => new FormQuestion(q.Id, q.Text, FormQuestionKind.Many, [new(q.ConsentLabel ?? "I agree")]),
        _ => new FormQuestion(q.Id, q.Text, FormQuestionKind.Text, null, q.Placeholder),
    }).ToList();

    /// <summary>The AI's suggestions, as the card's answers to start from.</summary>
    public IReadOnlyDictionary<string, FormAnswer> SuggestedForm()
    {
        Dictionary<string, FormAnswer> answers = [];
        foreach (var (id, value) in Suggested())
        {
            var q = Questions.First(x => x.Id == id);
            answers[id] = value switch
            {
                true when q.Type == Consent => new FormAnswer([q.ConsentLabel ?? "I agree"]),
                true => new FormAnswer([Yes]),
                false when q.Type == YesNo => new FormAnswer([No]),
                List<string> many => new FormAnswer(many),
                string text when q.Type == Text => new FormAnswer([], text),
                string pick => new FormAnswer([pick]),
                _ => FormAnswer.None,
            };
        }
        return answers;
    }

    /// <summary>The card's answers in the AI service's shape: a label, a list, true/false, or words.</summary>
    public Dictionary<string, object?> FromForm(IReadOnlyDictionary<string, FormAnswer> form)
    {
        Dictionary<string, object?> answers = [];
        foreach (var q in Questions)
        {
            var answer = form.TryGetValue(q.Id, out var given) ? given : FormAnswer.None;
            var words = string.IsNullOrWhiteSpace(answer.Text) ? null : answer.Text.Trim();
            answers[q.Id] = q.Type switch
            {
                YesNo => answer.Picked.Count == 0 ? null : answer.Picked[0] == Yes,
                Consent => answer.Picked.Count > 0,
                Many => words is null ? answer.Picked.ToList() : answer.Picked.Append(words).ToList(),
                One or Select => words ?? answer.Picked.FirstOrDefault(),
                _ => words,
            };
        }
        return answers;
    }

    private static List<ChoicePromptOption> Options(Question q) => q.Options.Select(o => new ChoicePromptOption(o.Label, o.Description)).ToList();

    private static bool Answered(Question q, object? answer) => q.Type switch
    {
        Consent => answer is true,
        YesNo => answer is bool,
        Many => answer is IReadOnlyCollection<string> { Count: > 0 },
        _ => answer is string text && !string.IsNullOrWhiteSpace(text),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // A suggested value arrives as a string ("true", "Balanced") or a plain JSON value.
    private static string? Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Array => string.Join(",", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => null,
        } : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
