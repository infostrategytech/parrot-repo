namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// How a <see cref="FormQuestion"/> is answered, and so which component draws it (DynamicQuestion maps each one
/// to its design-system component; dynamic-form-contract.md, section 2). One, Many and Text are the original
/// three; the rest came with the dynamic form (designer, 27 October 2026). Types not built yet are drawn as
/// Text, keeping what was asked for in <see cref="FormQuestion.RequestedType"/>.
/// </summary>
public enum FormQuestionKind
{
    One,
    Many,
    Text,
    Short,
    Email,
    Url,
    Phone,
    Money,
    Number,
    YesNo,
    Consent,
    Select,
    Search,
    Chips,
    Tags,
    Date,
    DateTime,
    Photos,
    /// <summary>Something to read between questions, with no answer.</summary>
    Note,
}

/// <summary>
/// One question in a <see cref="QuestionForm"/>. Choices are
/// <see cref="ChoicePromptOption"/>s, as in a <see cref="ChoicePrompt"/>;
/// <paramref name="AllowOther"/> adds a row to answer in their own words.
/// The rest is optional: leave it out and the question behaves as it always has.
/// </summary>
public sealed record FormQuestion(
    string Id,
    string Question,
    FormQuestionKind Kind,
    IReadOnlyList<ChoicePromptOption>? Options = null,
    string? Placeholder = null,
    bool AllowOther = false)
{
    /// <summary>Two or three words naming the question, for progress and summaries ("Payments").</summary>
    public string? Header { get; init; }

    /// <summary>One short line under the field.</summary>
    public string? Helper { get; init; }

    /// <summary>
    /// True: it must be answered before the main action. False: "(optional)" after the question. Null (left
    /// out): neither, as the form has always worked.
    /// </summary>
    public bool? Required { get; init; }

    /// <summary>Number and money: the lowest and highest value. Many, tags and photos: the fewest and most items.</summary>
    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    /// <summary>Text, short and tags: the most characters per answer or item.</summary>
    public int? MaxLength { get; init; }

    /// <summary>What to say when a rule fails, by rule: required, format, min, max, maxLength.</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }

    /// <summary>The type that was asked for when it's drawn as another (a type not built yet, drawn as Text).</summary>
    public string? RequestedType { get; init; }
}

/// <summary>
/// An answer to a <see cref="FormQuestion"/>: what was picked, in the order shown, and any words of their own;
/// and, for the questions that take them, a number, a whole phone number, a date and time, items, files or a tick.
/// </summary>
public sealed record FormAnswer(IReadOnlyList<string> Picked, string? Text = null)
{
    public static readonly FormAnswer None = new([], null);

    /// <summary>Number and money (naira).</summary>
    public decimal? Number { get; init; }

    /// <summary>Always whole: +2348035551234.</summary>
    public string? Phone { get; init; }

    public DateOnly? Date { get; init; }

    public TimeOnly? Time { get; init; }

    /// <summary>Tags: each item.</summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>Photos: each one, as the upload (or, until there is one, the picker) gave it.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Consent: ticked.</summary>
    public bool? Agreed { get; init; }

    public bool IsEmpty =>
        Picked.Count == 0 && string.IsNullOrWhiteSpace(Text) && Number is null && string.IsNullOrWhiteSpace(Phone)
        && Date is null && Time is null && (Items?.Count ?? 0) == 0 && (Files?.Count ?? 0) == 0 && Agreed != true;
}

/// <summary>What approving a form will do, shown above its questions: lines added, and the lines they replace.</summary>
public sealed record FormChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Replaced)
{
    public bool IsEmpty => Added.Count == 0 && Replaced.Count == 0;
}
