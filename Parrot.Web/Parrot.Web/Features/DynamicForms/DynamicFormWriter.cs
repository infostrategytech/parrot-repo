using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.DynamicForms;

/// <summary>
/// The answers, packed as the package reads them: a FormResult (dynamic-form-schema.json) with the form's id, how
/// it ended, and a Value per question by id, holding what that kind answers with:
/// - picked (the labels chosen, in the order shown) and text (their own words, from "Something else");
/// - text, for text-like questions and any type drawn as text;
/// - phone (always whole: +2348035551234), number (number and money, in naira), agreed (consent),
///   items (tags), files (photos), date (yyyy-MM-dd) and time (HH:mm, 24-hour).
/// A question left empty is an empty Value ({}). A note has no answer and isn't included.
/// "Decide for me" and "Skip for now" aren't checked first, so an answer that doesn't read as its kind
/// (a phone number that isn't whole, an amount that isn't one) is sent as their words instead.
/// </summary>
public static class DynamicFormWriter
{
    /// <summary>As written: plain characters (+234…, ₦, apostrophes), not escaped for a web page; it's sent as JSON, never shown as HTML.</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(DynamicForm form, IReadOnlyDictionary<string, FormAnswer> answers, DynamicFormEnd ended) =>
        Result(form, answers, ended).ToJsonString(Json);

    public static JsonObject Result(DynamicForm form, IReadOnlyDictionary<string, FormAnswer> answers, DynamicFormEnd ended)
    {
        var values = new JsonObject();
        foreach (var question in form.Questions)
        {
            if (question.Kind == FormQuestionKind.Note) continue;
            values[question.Id] = Value(question, answers.TryGetValue(question.Id, out var answer) ? answer : FormAnswer.None);
        }
        return new JsonObject
        {
            ["formId"] = form.Id,
            ["ended"] = ended switch
            {
                DynamicFormEnd.Decided => "decided",
                DynamicFormEnd.Skipped => "skipped",
                _ => "submitted",
            },
            ["answers"] = values,
        };
    }

    public static JsonObject Value(FormQuestion question, FormAnswer answer)
    {
        var value = new JsonObject();
        var words = (answer.Text ?? string.Empty).Trim();
        switch (question.Kind)
        {
            case FormQuestionKind.One or FormQuestionKind.Many or FormQuestionKind.YesNo
                or FormQuestionKind.Select or FormQuestionKind.Search or FormQuestionKind.Chips:
                if (answer.Picked.Count > 0) value["picked"] = Array(answer.Picked);
                if (words.Length > 0) value["text"] = words;
                break;

            case FormQuestionKind.Phone:
                if (answer.Phone is { } phone) value["phone"] = phone;
                else if (words.Length > 0) value["text"] = words;
                break;

            case FormQuestionKind.Money or FormQuestionKind.Number:
                if (answer.Number is { } number) value["number"] = number;
                else if (words.Length > 0) value["text"] = words;
                break;

            case FormQuestionKind.Consent:
                if (answer.Agreed is { } agreed) value["agreed"] = agreed;
                break;

            case FormQuestionKind.Tags:
                if (answer.Items is { Count: > 0 } items) value["items"] = Array(items);
                break;

            case FormQuestionKind.Photos:
                if (answer.Files is { Count: > 0 } files) value["files"] = Array(files);
                break;

            case FormQuestionKind.Date or FormQuestionKind.DateTime:
                if (answer.Date is { } date) value["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (question.Kind == FormQuestionKind.DateTime && answer.Date is not null && answer.Time is { } time)
                    value["time"] = time.ToString("HH:mm", CultureInfo.InvariantCulture);
                break;

            default:
                // Text, short, email, link, and any type drawn as text.
                if (words.Length > 0) value["text"] = words;
                break;
        }
        return value;
    }

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(item => JsonValue.Create(item))]);
}
