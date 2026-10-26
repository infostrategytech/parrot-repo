using System.Globalization;
using System.Text.RegularExpressions;

namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// The rules every form question follows (dynamic-form-contract.md, section 5): what counts as answered, and
/// what to say when an answer breaks a rule, written as how to fix it. The question's own words
/// (<see cref="FormQuestion.Errors"/>) win over these.
/// </summary>
public static partial class FormRules
{
    /// <summary>What's wrong with the answer, in words that say how to fix it; null when nothing is.</summary>
    public static string? Check(FormQuestion question, FormAnswer answer)
    {
        if (question.Kind == FormQuestionKind.Note) return null;
        if (!Answered(question, answer))
            return question.Required == true ? Say(question, "required", "Answer this to continue.") : null;

        switch (question.Kind)
        {
            case FormQuestionKind.Email when !Email().IsMatch((answer.Text ?? string.Empty).Trim()):
                return Say(question, "format", "Check this email.");
            case FormQuestionKind.Url when !Link().IsMatch((answer.Text ?? string.Empty).Trim()):
                return Say(question, "format", "Check this link.");
            case FormQuestionKind.Phone when answer.Phone is null:
                return Say(question, "format", "Check this number.");
            case FormQuestionKind.Money or FormQuestionKind.Number when answer.Number is null:
                return Say(question, "format", question.Kind == FormQuestionKind.Money ? "Use numbers, such as 45,000." : "Use a number, such as 12.");
            case FormQuestionKind.Money or FormQuestionKind.Number when OutOfRange(question, answer.Number!.Value):
                return Range(question, answer.Number!.Value);
            case FormQuestionKind.Text or FormQuestionKind.Short when question.MaxLength is { } most && (answer.Text ?? string.Empty).Trim().Length > most:
                return Say(question, "maxLength", $"Keep it under {most} characters.");
            case FormQuestionKind.Tags when question.MaxLength is { } longest && (answer.Items ?? []).Any(item => item.Length > longest):
                return Say(question, "maxLength", $"Keep each one under {longest} characters.");
        }

        var count = question.Kind switch
        {
            FormQuestionKind.Many => answer.Picked.Count + (string.IsNullOrWhiteSpace(answer.Text) ? 0 : 1),
            FormQuestionKind.Tags => answer.Items?.Count ?? 0,
            FormQuestionKind.Photos => answer.Files?.Count ?? 0,
            _ => (int?)null,
        };
        if (count is { } items && question.Min is { } fewest && items < fewest)
            return Say(question, "min", $"Pick at least {Plain(fewest)}.");
        return null;
    }

    /// <summary>Whether the question has an answer, by what its kind takes.</summary>
    public static bool Answered(FormQuestion question, FormAnswer answer) => question.Kind switch
    {
        FormQuestionKind.Consent => answer.Agreed == true,
        FormQuestionKind.Tags => (answer.Items?.Count ?? 0) > 0,
        FormQuestionKind.Photos => (answer.Files?.Count ?? 0) > 0,
        FormQuestionKind.Date or FormQuestionKind.DateTime => answer.Date is not null,
        FormQuestionKind.Note => true,
        _ => !answer.IsEmpty,
    };

    /// <summary>
    /// A phone number made whole: 0803…, 803…, 234… and +234… become +2348035551234; another country's number
    /// with its + is kept. Null when it can't be one.
    /// </summary>
    public static string? WholePhone(string? typed)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        var plus = text.StartsWith('+');
        var digits = new string([.. text.Where(char.IsDigit)]);
        string whole;
        if (plus) whole = "+" + digits;
        else if (digits.StartsWith("234") && digits.Length == 13) whole = "+" + digits;
        else if (digits.StartsWith('0') && digits.Length == 11) whole = "+234" + digits[1..];
        else if (digits.Length == 10 && digits[0] is '7' or '8' or '9') whole = "+234" + digits;
        else return null;
        if (whole.StartsWith("+234") && whole.Length != 14) return null;
        return WholeNumber().IsMatch(whole) ? whole : null;
    }

    /// <summary>A whole number to read: +234 803 555 1234 (others as they are).</summary>
    public static string SayPhone(string whole) =>
        whole.StartsWith("+234") && whole.Length == 14 ? $"+234 {whole[4..7]} {whole[7..10]} {whole[10..]}" : whole;

    /// <summary>An amount or count typed any common way: 45000, 45,000, ₦45,000, 45k. Null when it isn't one.</summary>
    public static decimal? ReadNumber(string? typed)
    {
        var text = (typed ?? string.Empty).Trim().Replace("₦", string.Empty).Replace("NGN", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", string.Empty).Replace(" ", string.Empty);
        if (text.Length == 0) return null;
        var times = 1m;
        if (text.EndsWith('k') || text.EndsWith('K')) { times = 1_000m; text = text[..^1]; }
        else if (text.EndsWith('m') || text.EndsWith('M')) { times = 1_000_000m; text = text[..^1]; }
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value * times : null;
    }

    /// <summary>A number with its thousands grouped, as it's shown once typed: 45,000.</summary>
    public static string Grouped(decimal value) =>
        value.ToString(value == decimal.Truncate(value) ? "#,##0" : "#,##0.##", CultureInfo.InvariantCulture);

    private static bool OutOfRange(FormQuestion question, decimal value) =>
        (question.Min is { } low && value < low) || (question.Max is { } high && value > high);

    // Too low says the min rule's words, too high the max rule's; the default names the range either way.
    private static string Range(FormQuestion question, decimal value)
    {
        var rule = question.Min is { } floor && value < floor ? "min" : "max";
        var words = (question.Min, question.Max) switch
        {
            ({ } low, { } high) => $"Use a number from {Plain(low)} to {Plain(high)}.",
            ({ } low, null) => $"Use {Plain(low)} or more.",
            (null, { } high) => $"Use {Plain(high)} or less.",
            _ => "Use a number.",
        };
        return Say(question, rule, words);
    }

    private static string Plain(decimal value) => Grouped(value);

    private static string Say(FormQuestion question, string rule, string otherwise) =>
        question.Errors is { } errors && errors.TryGetValue(rule, out var words) && !string.IsNullOrWhiteSpace(words) ? words : otherwise;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex Email();

    // A website or social link, with or without https:// (instagram.com/yourshop).
    [GeneratedRegex(@"^(https?://)?([\w-]+\.)+[a-z]{2,}(/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex WholeNumber();
}
