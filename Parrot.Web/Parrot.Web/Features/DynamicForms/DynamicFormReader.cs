using System.Globalization;
using System.Text.Json;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.DynamicForms;

/// <summary>
/// Reads a form the backend sends (dynamic-form-schema.json, version 1) into one the app draws. It never lets
/// one mistake stop the form (dynamic-form-contract.md, section 6, The fallback):
/// - a type the app doesn't draw yet (hours, file, time, daterange, address, list, or any new one) becomes a
///   text question with the same question, helper and placeholder, so the owner can still answer in their words;
/// - a choice question without choices becomes a text question too;
/// - a question without an id or words, or with an id already used, is left out;
/// - fields it doesn't know (styling included) are ignored.
/// Only a form with no question left to show can't be drawn. Everything it changed is listed in Problems.
/// </summary>
public static class DynamicFormReader
{
    /// <summary>The contract version this app reads. Minor versions only add, so later ones are read the same.</summary>
    public const int Version = 1;

    private static readonly Dictionary<string, FormQuestionKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = FormQuestionKind.One,
        ["many"] = FormQuestionKind.Many,
        ["text"] = FormQuestionKind.Text,
        ["short"] = FormQuestionKind.Short,
        ["email"] = FormQuestionKind.Email,
        ["url"] = FormQuestionKind.Url,
        ["phone"] = FormQuestionKind.Phone,
        ["money"] = FormQuestionKind.Money,
        ["number"] = FormQuestionKind.Number,
        ["yesno"] = FormQuestionKind.YesNo,
        ["consent"] = FormQuestionKind.Consent,
        ["select"] = FormQuestionKind.Select,
        ["search"] = FormQuestionKind.Search,
        ["chips"] = FormQuestionKind.Chips,
        ["tags"] = FormQuestionKind.Tags,
        ["date"] = FormQuestionKind.Date,
        ["datetime"] = FormQuestionKind.DateTime,
        ["photos"] = FormQuestionKind.Photos,
        ["note"] = FormQuestionKind.Note,
    };

    private static readonly HashSet<FormQuestionKind> Chosen =
        [FormQuestionKind.One, FormQuestionKind.Many, FormQuestionKind.Select, FormQuestionKind.Search, FormQuestionKind.Chips];

    public static DynamicFormRead Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException exception)
        {
            return new DynamicFormRead(null, [$"The form isn't valid JSON: {exception.Message}"]);
        }
    }

    public static DynamicFormRead Read(JsonElement root)
    {
        var problems = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return new DynamicFormRead(null, ["The form isn't an object."]);

        if (Number(root, "version") is { } version && version != Version)
            problems.Add($"Version {version.ToString(CultureInfo.InvariantCulture)} was sent; this app reads version {Version}. Read as version {Version}.");

        var id = Text(root, "id");
        if (id is null) problems.Add("The form has no id; its answers can't be matched to it.");
        var title = Text(root, "title");
        if (title is null) problems.Add("The form has no title.");
        var submit = Text(root, "submitLabel");
        if (submit is null) problems.Add("The form has no submitLabel; \"Continue\" is used.");

        var questions = new List<FormQuestion>();
        var prefilled = new Dictionary<string, FormAnswer>();
        if (root.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (ReadQuestion(item, index, problems) is not { } question) continue;
                if (questions.Any(other => other.Id == question.Id))
                {
                    problems.Add($"Question {index}: the id \"{question.Id}\" is used twice; left out.");
                    continue;
                }
                questions.Add(question);
                if (item.TryGetProperty("value", out var value) && question.Kind != FormQuestionKind.Note && ReadValue(value, question) is { IsEmpty: false } given)
                    prefilled[question.Id] = given;
            }
        }
        if (questions.Count == 0)
        {
            problems.Add("The form has no question that can be shown.");
            return new DynamicFormRead(null, problems);
        }
        if (questions.Count > 4) problems.Add($"The form has {questions.Count} questions; a step should have at most 4. All are shown.");

        FormChanges? changes = null;
        if (root.TryGetProperty("changes", out var changed) && changed.ValueKind == JsonValueKind.Object)
            changes = new FormChanges(Strings(changed, "added") ?? [], Strings(changed, "replaced") ?? []);

        var form = new DynamicForm(
            id ?? string.Empty,
            title ?? string.Empty,
            Text(root, "subtitle"),
            changes is { IsEmpty: false } ? changes : null,
            questions,
            prefilled,
            submit ?? "Continue",
            Flag(root, "allowDecide") ?? false,
            Flag(root, "allowSkip") ?? false,
            Number(root, "step") is { } step ? (int)step : null,
            Number(root, "steps") is { } steps ? (int)steps : null);
        return new DynamicFormRead(form, problems);
    }

    private static FormQuestion? ReadQuestion(JsonElement item, int index, List<string> problems)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"Question {index} isn't an object; left out.");
            return null;
        }
        var id = Text(item, "id");
        var words = Text(item, "question");
        if (id is null || words is null)
        {
            problems.Add($"Question {index} has no {(id is null ? "id" : "question")}; left out.");
            return null;
        }

        var type = Text(item, "type") ?? "text";
        string? requested = null;
        if (!Kinds.TryGetValue(type, out var kind))
        {
            problems.Add($"Question \"{id}\": the type \"{type}\" isn't drawn yet; shown as text.");
            kind = FormQuestionKind.Text;
            requested = type;
        }

        var options = Options(item);
        if (Chosen.Contains(kind) && options.Count == 0)
        {
            problems.Add($"Question \"{id}\": \"{type}\" needs options; shown as text.");
            kind = FormQuestionKind.Text;
            requested = type;
        }

        return new FormQuestion(
            id,
            words,
            kind,
            options.Count > 0 ? options : null,
            Text(item, "placeholder"),
            kind is FormQuestionKind.One or FormQuestionKind.Many && (Flag(item, "allowOther") ?? false))
        {
            Header = Text(item, "header"),
            Helper = Text(item, "helper"),
            // Required unless said otherwise (the schema's default); a note is never answered.
            Required = kind == FormQuestionKind.Note ? false : Flag(item, "required") ?? true,
            Min = Number(item, "min"),
            Max = Number(item, "max"),
            MaxLength = Number(item, "maxLength") is { } most ? (int)most : null,
            Errors = Errors(item),
            RequestedType = requested,
        };
    }

    // A pre-filled answer, read by what the question takes.
    private static FormAnswer? ReadValue(JsonElement value, FormQuestion question)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var picked = Strings(value, "picked") ?? [];
        var text = Text(value, "text");
        var answer = new FormAnswer(picked, text);
        switch (question.Kind)
        {
            case FormQuestionKind.Phone when Text(value, "phone") is { } phone:
                var whole = FormRules.WholePhone(phone);
                return answer with { Phone = whole, Text = whole is { } saved && saved.StartsWith("+234") ? "0" + saved[4..] : phone };
            case FormQuestionKind.Money or FormQuestionKind.Number when Number(value, "number") is { } number:
                return answer with { Number = number, Text = FormRules.Grouped(number) };
            case FormQuestionKind.Consent:
                return answer with { Agreed = Flag(value, "agreed") };
            case FormQuestionKind.Tags:
                return answer with { Items = Strings(value, "items") };
            case FormQuestionKind.Photos:
                return answer with { Files = Strings(value, "files") };
            case FormQuestionKind.Date or FormQuestionKind.DateTime:
                return answer with
                {
                    Date = DateOnly.TryParseExact(Text(value, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                    Time = TimeOnly.TryParseExact(Text(value, "time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null,
                };
        }
        // Drawn as text (a type not drawn yet): whatever words came, so the owner can check them.
        if (question.RequestedType is not null && text is null && picked.Count > 0) return new FormAnswer([], string.Join(", ", picked));
        return answer;
    }

    private static List<ChoicePromptOption> Options(JsonElement item)
    {
        var options = new List<ChoicePromptOption>();
        if (!item.TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array) return options;
        var recommended = false;
        foreach (var option in list.EnumerateArray())
        {
            var label = option.ValueKind == JsonValueKind.String ? option.GetString() : Text(option, "label");
            if (string.IsNullOrWhiteSpace(label) || options.Any(other => other.Label == label)) continue;
            // At most one is recommended: the first one marked.
            var best = !recommended && option.ValueKind == JsonValueKind.Object && (Flag(option, "recommended") ?? false);
            recommended |= best;
            options.Add(new ChoicePromptOption(label, option.ValueKind == JsonValueKind.Object ? Text(option, "description") : null) { Recommended = best });
        }
        return options;
    }

    private static IReadOnlyDictionary<string, string>? Errors(JsonElement item)
    {
        if (!item.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object) return null;
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in errors.EnumerateObject())
            if (rule.Value.ValueKind == JsonValueKind.String && rule.Value.GetString() is { Length: > 0 } line) words[rule.Name] = line;
        return words.Count > 0 ? words : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Trim().Length > 0 ? text.Trim() : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static List<string>? Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => item.Trim().Length > 0)]
            : null;
}
