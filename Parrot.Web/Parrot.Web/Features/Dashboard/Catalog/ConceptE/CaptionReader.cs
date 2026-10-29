using System.Globalization;
using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.Catalog.ConceptE;

/// <summary>
/// Reads what an owner writes about one item, the way they'd post it ("CHELSEA in grey. UK size 6 - 12:
/// 58,500 …"), into what the agent needs: a name, a price in one of its shapes, the choices, how long it
/// takes, and the good-to-know lines. Nothing is saved: the owner sees it as "What your agent will say" and
/// fixes it there. The real reader belongs to the backend (catalog-backend-requests: reading posts and
/// price lists); this demo stands in for it with the patterns the research found.
/// </summary>
public interface ICaptionReader
{
    bool CanRead { get; }

    Task<CaptionReading> ReadAsync(string caption, CancellationToken cancellationToken = default);
}

/// <summary>What was understood from the owner's words. Empty parts weren't found.</summary>
public sealed class CaptionReading
{
    public string Name { get; init; } = "";
    public CatalogKind? Kind { get; init; }

    /// <summary>Null when no price was found.</summary>
    public PriceType? PriceType { get; init; }

    public decimal? Price { get; init; }
    public string PriceUnit { get; init; } = "";

    /// <summary>The choice that sets the price ("Length": 8 inches, 10 inches), with a price for each value.</summary>
    public ItemOption? PriceOption { get; init; }

    public List<decimal> OptionPrices { get; init; } = [];

    /// <summary>Choices that don't change the price: colours, flavours, sizes listed without prices.</summary>
    public List<ItemOption> Choices { get; init; } = [];

    /// <summary>
    /// The versions written, when each is named by more than one thing ("UK 6 blue 50k", "iPhone 14 Pro 128gb
    /// blue 1.15m"): what makes them different (Size, Colour), and each version's values, in the order of
    /// <see cref="OptionPrices"/>. Only these versions exist: their values are never multiplied out.
    /// </summary>
    public List<string> VersionColumns { get; init; } = [];

    public List<List<string>> Versions { get; init; } = [];

    public string LeadTime { get; init; } = "";
    public bool MadeToOrder { get; init; }

    /// <summary>Facts about this item the agent can repeat ("Length can be adjusted when ordering.").</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>Lines that read as true of everything sold (delivery, payment, how to order).</summary>
    public List<string> ForBusiness { get; init; } = [];

    /// <summary>Amounts read with a guess, for the owner to confirm.</summary>
    public List<string> Checks { get; init; } = [];

    /// <summary>
    /// The owner's words to keep as the item's description: everything but the prices, the choices and how
    /// long it takes (kept in their own places, so a price changed later is never contradicted by old words),
    /// and the lines that belong to the whole business.
    /// </summary>
    public string Description { get; init; } = "";

    public bool Found => Name.Length > 0 || PriceType is not null || Choices.Count > 0 || Notes.Count > 0;

    /// <summary>Puts the price, its choices and the other choices on the item, replacing what was there.</summary>
    public void ApplyOffer(CatalogEntry entry)
    {
        entry.PriceUnit = PriceUnit;
        entry.PriceType = PriceType ?? Catalog.PriceType.Fixed;
        entry.Price = null;

        // A version for each priced line, written as it reads ("UK 6 Blue", "128GB Blue UK used"), at its own
        // price. A choice that doesn't change the price (colours, flavours) makes a version of each, at the same
        // price; one with a single value ("in grey") says nothing that differs, so it stays in the words.
        var versions = new List<(string Name, decimal? Price)>();
        if (VersionColumns.Count > 0 && Versions.Count == OptionPrices.Count)
            versions = [.. Versions.Select((row, index) => (string.Join(' ', row), (decimal?)OptionPrices[index]))];
        else if (PriceOption is { } option)
            versions = [.. option.Values.Select((value, index) => (value, index < OptionPrices.Count ? (decimal?)OptionPrices[index] : null))];
        var varying = Choices.Where(choice => choice.Values.Count > 1 && !VersionColumns.Contains(choice.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (versions.Count == 0 && varying.Count > 0) versions = [(string.Empty, Price)];
        foreach (var choice in varying)
            versions = [.. versions.SelectMany(version => choice.Values.Select(value => (string.Join(' ', new[] { version.Name, value }.Where(part => part.Length > 0)), version.Price)))];
        versions = [.. versions.Select(version => (version.Name.Replace(" / ", " ").Trim(), version.Price)).Where(version => version.Item1.Length > 0)
            .DistinctBy(version => version.Item1, StringComparer.OrdinalIgnoreCase)];

        if (versions.Count < 2)
        {
            entry.Options = [];
            entry.Variants = [];
            entry.Price = versions is [{ Price: { } only }] ? only : Price;
            return;
        }
        entry.Options = [new ItemOption { Name = PriceWords.VariantList, Values = [.. versions.Select(version => version.Name)] }];
        entry.Variants = [.. versions.Select(version => new ItemVariant { Key = version.Name, Price = version.Price })];
        entry.Price = PriceType == Catalog.PriceType.From && versions.Any(version => version.Price is not null) ? versions.Min(version => version.Price) : null;
    }
}

/// <summary>
/// The demo's reader: patterns from real captions (products-pricing-research.md). "k" and "m" shorthand,
/// ₦, N, # or no currency, "naira"; a price list run together on one line, as TikTok shows it; per yard,
/// per person, for 5 yards; from and starts from; "DM for price"; colours and flavours; production time.
/// </summary>
public sealed partial class DemoCaptionReader : ICaptionReader
{
    public bool CanRead => true;

    public async Task<CaptionReading> ReadAsync(string caption, CancellationToken cancellationToken = default)
    {
        // Demo latency belongs to the reader, not the screen's production interaction states.
        await Task.Delay(650, cancellationToken);
        return Read(caption);
    }

    private sealed record Token(int Line, int Start, int End, int ValueStart, decimal Amount, bool From, string Label, string Unit, string? Check);

    public static CaptionReading Read(string caption)
    {
        var lines = Clean(caption).Split('\n').Select(line => Spaces().Replace(line, " ").Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0) return new CaptionReading();

        var tokens = new List<Token>();
        for (var index = 0; index < lines.Count; index++) tokens.AddRange(Prices(lines[index], index));

        // ── The price ──────────────────────────────────────────────────────
        PriceType? priceType = null;
        decimal? price = null;
        var unit = string.Empty;
        ItemOption? priceOption = null;
        var optionPrices = new List<decimal>();
        var versionColumns = new List<string>();
        var versionRows = new List<List<string>>();
        string? namePrefix = null;
        var notes = new List<string>();
        var checks = tokens.Where(token => token.Check is not null).Select(token => token.Check!).ToList();

        var labelled = tokens.Select(token => (token, value: Value(token.Label, out var before), before)).ToList();
        if (tokens.Count == 0)
        {
            if (AskForPrice().IsMatch(string.Join(" ", lines))) priceType = Catalog.PriceType.OnRequest;
        }
        else if (tokens.Count == 2 && labelled[1].value.Length == 0 && tokens[0].Line == tokens[1].Line)
        {
            // "₦15k - 20k": a range. Shown as a starting price, and its top said in words.
            priceType = Catalog.PriceType.From;
            price = Math.Min(tokens[0].Amount, tokens[1].Amount);
            notes.Add($"Up to {PriceWords.Naira(Math.Max(tokens[0].Amount, tokens[1].Amount))}, depending on what's chosen.");
            unit = tokens[0].Unit.Length > 0 ? tokens[0].Unit : tokens[1].Unit;
            namePrefix = labelled[0].value.Length > 0 ? labelled[0].value : labelled[0].before;
        }
        else if (tokens.Count == 1 || labelled.Count(row => row.value.Length > 0) < 2)
        {
            priceType = tokens[0].From ? Catalog.PriceType.From : Catalog.PriceType.Fixed;
            price = tokens[0].Amount;
            unit = tokens[0].Unit;
            // The words before a single price, whole (a choice's value is cut short; a name mustn't be).
            namePrefix = Spaces().Replace(LabelNoise().Replace(tokens[0].Label, " "), " ").Trim(' ', ':', ';', '-', '–', '—', '=', '|', '@', ',', '/', '(', ')');
        }
        else
        {
            var rows = labelled.Where(row => row.value.Length > 0).GroupBy(row => row.value, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            priceOption = new ItemOption { Name = OptionName(rows.Select(row => row.value)), Values = [.. rows.Select(row => row.value)] };
            optionPrices = [.. rows.Select(row => row.token.Amount)];
            priceType = rows.Any(row => row.token.From) ? Catalog.PriceType.From : Catalog.PriceType.Fixed;
            unit = rows.Select(row => row.token.Unit).FirstOrDefault(found => found.Length > 0) ?? string.Empty;
            namePrefix = labelled[0].before;
            // Each line names a version by more than one thing: a column for each, and only the versions written.
            if (Versions([.. labelled.Select(row => (Label: Whole(row.token.Label), row.token.Amount)).Where(row => row.Label.Length > 0)]) is { } split)
            {
                versionColumns = split.Columns;
                versionRows = split.Rows;
                optionPrices = split.Prices;
                if (split.Common.Length > 0) namePrefix = split.Common;
            }
        }

        // ── The name ───────────────────────────────────────────────────────
        var colours = new List<string>();
        var name = Name(lines, tokens, namePrefix, colours);

        // ── Choices that don't change the price ────────────────────────────
        var all = string.Join("\n", lines);
        var choices = new List<ItemOption>();
        foreach (Match match in ListedColours().Matches(all)) colours.AddRange(SplitList(CutList(match.Groups["list"].Value)));
        foreach (Match match in InColour().Matches(all)) colours.Add(Title(match.Groups["colour"].Value));
        // A colour the versions already name is theirs, not a choice for every version.
        if (colours.Count > 0 && !versionColumns.Contains("Colour")) choices.Add(new ItemOption { Name = "Colour", Values = [.. colours.Select(Title).Distinct(StringComparer.OrdinalIgnoreCase)] });
        if (ListedFlavours().Match(all) is { Success: true } flavours)
            choices.Add(new ItemOption { Name = "Flavour", Values = [.. SplitList(CutList(flavours.Groups["list"].Value)).Select(Title).Distinct(StringComparer.OrdinalIgnoreCase)] });
        if (priceOption is null && ListedSizes().Match(all) is { Success: true } sizes)
            choices.Insert(0, new ItemOption { Name = "Size", Values = [.. SplitList(sizes.Groups["list"].Value).Select(NormaliseValue).Where(value => value.Length > 0)] });

        // ── How long it takes ──────────────────────────────────────────────
        var lead = LeadTime().Match(all);
        var leadTime = lead.Success ? Spaces().Replace(lead.Groups["time"].Value, " ").Trim().ToLowerInvariant() : string.Empty;
        var madeToOrder = MadeToOrder().IsMatch(all);

        // ── Good to know, and what belongs to the whole business ───────────
        var forBusiness = new List<string>();
        var kept = new List<string>();
        foreach (var sentence in Sentences(lines))
        {
            if (!Prices(sentence, 0).Any() && !AskForPrice().IsMatch(sentence) && !Business().IsMatch(sentence)
                && !ListedColours().IsMatch(sentence) && !ListedFlavours().IsMatch(sentence) && !ListedSizes().IsMatch(sentence) && !LeadTime().IsMatch(sentence))
                kept.Add(Sentence(sentence));
            if (Prices(sentence, 0).Any()) continue;
            if (name.Length > 0 && sentence.Contains(name, StringComparison.OrdinalIgnoreCase) && sentence.Length < name.Length + 40) continue;
            if (ListedColours().IsMatch(sentence) || ListedFlavours().IsMatch(sentence) || ListedSizes().IsMatch(sentence) || LeadTime().IsMatch(sentence)) continue;
            if (AskForPrice().IsMatch(sentence)) continue;
            if (Business().IsMatch(sentence)) { forBusiness.Add(Sentence(sentence)); continue; }
            if (Fluff().IsMatch(sentence)) continue;
            if (sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) continue;
            notes.Add(Sentence(sentence));
        }

        return new CaptionReading
        {
            Name = name,
            Kind = Kind(all),
            PriceType = priceType,
            Price = price,
            PriceUnit = unit,
            PriceOption = priceOption,
            OptionPrices = optionPrices,
            Choices = choices,
            VersionColumns = versionColumns,
            Versions = versionRows,
            LeadTime = leadTime,
            MadeToOrder = madeToOrder || leadTime.Length > 0 && Production().IsMatch(all),
            Notes = [.. notes.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)],
            ForBusiness = [.. forBusiness.Distinct(StringComparer.OrdinalIgnoreCase).Take(4)],
            Checks = checks,
            // A range's top has no place of its own yet, so its sentence stays in the words.
            Description = string.Join(" ", notes.Where(note => note.StartsWith("Up to ", StringComparison.Ordinal)).Concat(kept).Distinct()),
        };
    }

    /// <summary>Naira as people type it: "58,500", "₦12000", "N275,000", "#8,500", "7k", "1.2m", "33,000 naira". Null when it isn't an amount.</summary>
    public static decimal? Money(string? text)
    {
        var tokens = Prices(Clean(text ?? string.Empty), 0, bare: true).ToList();
        return tokens.Count == 1 ? tokens[0].Amount : null;
    }

    // ── Reading amounts ────────────────────────────────────────────────────

    private static IEnumerable<Token> Prices(string line, int index, bool bare = false)
    {
        var previousEnd = 0;
        foreach (Match match in Amount().Matches(line))
        {
            var numberText = match.Groups["num"].Value.Replace(" ", string.Empty).Replace(",", string.Empty);
            if (!decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) continue;
            var marked = match.Groups["cur"].Success || match.Groups["naira"].Success;
            var multiplier = match.Groups["mult"].Value.Trim().ToLowerInvariant();
            var after = line[match.Index..][match.Length..];
            // A measure, not a price: 10ml, 8 inches, 24 hours, 2 yards, 220g, 2x4, 50%.
            if (!marked && multiplier.Length == 0 && MeasureAfter().IsMatch(after)) continue;
            if (!marked && multiplier == "m" && MeasureAfter().IsMatch(match.Groups["mult"].Value + after)) continue;
            string? check = null;
            var amount = number;
            if (multiplier == "k") amount = number * 1000;
            else if (multiplier == "m")
            {
                // "1.2m" is ₦1,200,000; "N1,690m" most likely means ₦1,690,000: said back to confirm.
                if (numberText.Length >= 4 && !numberText.Contains('.')) { amount = number * 1000; check = $"We read \"{match.Value.Trim()}\" as {PriceWords.Naira(amount)}."; }
                else amount = number * 1_000_000;
            }
            var plain = !marked && multiplier.Length == 0;
            if (plain && !bare && amount < 1000) continue;
            if (plain && !bare && amount is >= 1990 and <= 2035 && !numberText.Contains(',')) continue;
            if (plain && numberText.Length >= 10) continue;
            if (amount <= 0) continue;

            var label = line[previousEnd..match.Index];
            var unitMatch = UnitAfter().Match(after);
            var unit = string.Empty;
            if (unitMatch.Success)
            {
                var count = unitMatch.Groups["count"].Value;
                var word = Units(unitMatch.Groups["unit"].Value, count is "" or "1");
                unit = count is "" or "1" ? $"per {word}" : $"per {count} {word}";
            }
            else if (UnitBefore().Match(label) is { Success: true } before)
            {
                unit = $"per {Units(before.Groups["unit"].Value, true)}";
                label = label[..before.Index];
            }
            previousEnd = match.Index + match.Length + (unitMatch.Success ? unitMatch.Length : 0);
            yield return new Token(index, match.Index, previousEnd, match.Groups["num"].Index, decimal.Round(amount, 2), match.Groups["from"].Success, label, unit, check);
        }
    }

    private static string Units(string word, bool one)
    {
        var lower = word.ToLowerInvariant();
        var singular = lower switch
        {
            "yd" or "yds" or "yards" => "yard",
            "hr" or "hrs" or "hours" => "hour",
            "persons" or "people" or "pax" => "person",
            "plates" => "plate",
            "packs" => "pack",
            "bundles" => "bundle",
            "pieces" or "pcs" or "pc" => "piece",
            "ml" => "ml",
            "litres" or "liters" or "liter" or "l" => "litre",
            "nights" => "night",
            "days" => "day",
            "weeks" => "week",
            "months" => "month",
            _ => lower.TrimEnd('s') is { Length: > 1 } trimmed ? trimmed : lower,
        };
        return one || singular is "ml" ? singular : singular + "s";
    }

    // ── Labels: the words before an amount ─────────────────────────────────

    /// <summary>The choice an amount belongs to ("UK 6–12", "10ml", "Big"), and the words before it.</summary>
    private static string Value(string label, out string before)
    {
        var text = LabelNoise().Replace(label, " ");
        text = Spaces().Replace(text, " ").Trim(' ', ':', ';', '-', '–', '—', '=', '|', '@', ',', '.', '/', '(', ')');
        before = string.Empty;
        if (text.Length == 0) return string.Empty;
        // "Bob wigs pricelist 8 inches": the measure is the choice; the words before it name the item.
        if (MeasureAtEnd().Match(text) is { Success: true } measure && measure.Index > 0)
        {
            before = text[..measure.Index].Trim(' ', ':', '-', ',');
            return NormaliseValue(measure.Value);
        }
        return NormaliseValue(text);
    }

    private static string NormaliseValue(string value)
    {
        var text = SizeWord().Replace(value, " ");
        text = DashBetweenNumbers().Replace(text, "$1–$2");
        text = Spaces().Replace(text, " ").Trim(' ', ':', '-', '–', ',', '.');
        if (text.Length > 0 && text == text.ToUpperInvariant() && text.Any(char.IsLetter) && !text.Any(char.IsDigit) && text.Length > 3) text = Title(text);
        return text.Length > 40 ? text[..40].Trim() : text;
    }

    private static string OptionName(IEnumerable<string> values)
    {
        var all = string.Join(" ", values).ToLowerInvariant();
        if (Regex.IsMatch(all, @"inch|""|”|\bcm\b")) return "Length";
        if (Regex.IsMatch(all, @"\d\s*(ml|cl|l\b|litre|liter)")) return "Size";
        if (Regex.IsMatch(all, @"\d\s*(gb|tb)\b")) return "Storage";
        if (Regex.IsMatch(all, @"\d\s*(kg|g\b|grams?)")) return "Weight";
        if (Regex.IsMatch(all, @"yard")) return "Quantity";
        if (Regex.IsMatch(all, @"\blooks?\b|\bpackage\b|\bplan\b")) return "Package";
        if (Regex.IsMatch(all, @"\buk\b|\bus\b|\b(xs|s|m|l|xl|xxl)\b|\bbig\b|\bsmall\b|\bmedium\b|\blarge\b|\bmini\b|^\s*\d+(\s|$)|\btray\b|\bcooler\b|\bbowl\b|\bpack\b|\bbox\b")) return "Size";
        return "Option";
    }

    // ── Versions: lines that each name a version by more than one thing ────

    private sealed record Split(List<string> Columns, List<List<string>> Rows, List<decimal> Prices, string Common);

    // What tells versions apart, looked for in this order (a condition before a size, so "UK used" isn't a UK size).
    private static readonly (string Column, Func<Regex> Pattern)[] Parts =
        [("Condition", ConditionPart), ("Storage", StoragePart), ("Length", LengthPart), ("Size", SizePart), ("Colour", ColourPart)];

    // The order the columns show in: what it is first, then what it's like.
    private static readonly string[] ColumnOrder = ["Model", "Size", "Length", "Storage", "Colour", "Condition"];

    /// <summary>A price's whole label, without the words that only announce a price.</summary>
    private static string Whole(string label) =>
        Spaces().Replace(LabelNoise().Replace(label, " "), " ").Trim(' ', ':', ';', '-', '–', '—', '=', '|', '@', ',', '.', '/', '(', ')');

    /// <summary>
    /// "UK 6 blue / UK 12 red / UK 14 yellow", "iPhone 14 Pro 128gb blue / iPhone 14 Pro Max 256gb black": the things
    /// each line names, as columns, when the lines differ by at least two of them. Null when they don't, or when
    /// some lines name something the others don't: those are read the simple way, as one choice.
    /// </summary>
    private static Split? Versions(List<(string Label, decimal Amount)> lines)
    {
        lines = [.. lines.DistinctBy(line => line.Label, StringComparer.OrdinalIgnoreCase)];
        if (lines.Count < 2) return null;

        var found = lines.Select(line => Find(line.Label)).ToList();
        if (Parts.Any(part => found.Any(parts => parts.ContainsKey(part.Column)) && !found.All(parts => parts.ContainsKey(part.Column)))) return null;
        // A thing every line shares (all "UK used") doesn't tell them apart, so it stays in the words.
        var varying = Parts.Select(part => part.Column)
            .Where(column => found.All(parts => parts.ContainsKey(column)) && found.Select(parts => parts[column].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();

        var rests = lines.Select((line, index) =>
        {
            var rest = line.Label;
            foreach (var (_, start, length) in varying.Select(column => found[index][column]).OrderByDescending(part => part.Start))
                rest = rest[..start] + " " + rest[(start + length)..];
            return Spaces().Replace(rest, " ").Trim(' ', ',', '-', '–', '/', ':', '|', '(', ')', '.');
        }).ToList();

        // What's left names the item when it's the same on every line, or written once on the first ("iPhone 14 Pro
        // 128gb blue 1.15m, 256gb black 1.3m"); when it differs, it's the model ("iPhone 14 Pro", "iPhone 14 Pro Max").
        var common = string.Empty;
        string? restColumn = null;
        if (rests.All(rest => string.Equals(rest, rests[0], StringComparison.OrdinalIgnoreCase)) || rests[0].Length > 0 && rests.Skip(1).All(rest => rest.Length == 0))
            common = rests[0];
        else if (rests.All(rest => rest.Length > 0))
        {
            restColumn = rests.All(rest => BareNumber().IsMatch(rest)) && !varying.Contains("Size") ? "Size" : "Model";
            var words = rests.Select(rest => rest.Split(' ')).ToList();
            common = string.Join(' ', words[0].TakeWhile((word, at) => words.All(other => other.Length > at && string.Equals(other[at], word, StringComparison.OrdinalIgnoreCase))));
        }
        else return null;

        var columns = ColumnOrder.Where(column => varying.Contains(column) || column == restColumn).ToList();
        if (columns.Count < 2) return null;
        var rows = lines.Select((_, index) => columns.Select(column => column == restColumn ? Pretty(column, rests[index]) : found[index][column].Value).ToList()).ToList();
        if (rows.DistinctBy(row => string.Join(" / ", row), StringComparer.OrdinalIgnoreCase).Count() != rows.Count) return null;
        return new Split(columns, rows, [.. lines.Select(line => line.Amount)], common.Length > 0 && common == common.ToUpperInvariant() ? Title(common) : common);
    }

    // Each thing a line names, where it is: the first of each kind, never overlapping another.
    private static Dictionary<string, (string Value, int Start, int Length)> Find(string label)
    {
        var parts = new Dictionary<string, (string Value, int Start, int Length)>();
        foreach (var (column, pattern) in Parts)
        {
            var match = pattern().Matches(label).FirstOrDefault(candidate => !parts.Values.Any(part => candidate.Index < part.Start + part.Length && part.Start < candidate.Index + candidate.Length));
            if (match is not null) parts[column] = (Pretty(column, match.Value), match.Index, match.Length);
        }
        return parts;
    }

    // As people would read it back: UK 6, XL, 50ml, 128GB, Blue, UK used.
    private static string Pretty(string column, string text)
    {
        var value = Spaces().Replace(text, " ").Trim();
        if (value.Length == 0) return value;
        return column switch
        {
            "Storage" => value.Replace(" ", string.Empty).ToUpperInvariant(),
            "Size" when LetterSize().IsMatch(value) => value.ToUpperInvariant(),
            "Size" when Region().IsMatch(value) => Region().Replace(NormaliseValue(value), match => match.Value.ToUpperInvariant()),
            "Size" when value.Any(char.IsDigit) => value.Replace(" ", string.Empty).ToLowerInvariant(),
            "Size" or "Colour" => Title(value),
            "Length" => NormaliseValue(value).Replace("\"", " inches").Replace("”", " inches"),
            "Condition" => Region().Replace(char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant(), match => match.Value.ToUpperInvariant()),
            _ => value == value.ToUpperInvariant() && value.Length > 3 ? Title(value) : value,
        };
    }

    // ── The name ───────────────────────────────────────────────────────────

    private static string Name(List<string> lines, List<Token> tokens, string? prefix, List<string> colours)
    {
        string candidate;
        var first = lines.FindIndex(line => line.Any(char.IsLetter));
        if (first < 0) return string.Empty;
        // "BLACK but make it OUTSTANDING / Introducing QUEENCY": the line that introduces it names it, even after a
        // hook (and misspelt: "Introducting").
        var introduced = lines.FindIndex(line => Opening().IsMatch(line) && Opening().Replace(line, string.Empty).Trim().Length > 0);
        if (introduced > first && !tokens.Any(token => token.Line == introduced)) candidate = lines[introduced];
        else if (tokens.FirstOrDefault()?.Line == first) candidate = prefix ?? string.Empty;
        else candidate = lines[first];

        candidate = Opening().Replace(candidate, string.Empty);
        candidate = LabelNoise().Replace(candidate, " ");
        // "CHELSEA In this Gorgeous Grey": the colour is a choice, not part of the name.
        if (InColour().Match(candidate) is { Success: true } colour)
        {
            colours.Add(Title(colour.Groups["colour"].Value));
            candidate = candidate[..colour.Index];
        }
        // A short phrase just before a price names the thing priced: "A little indigo for every day. Our cotton
        // adire scarves are ₦12,000" names the scarves. Otherwise the first sentence names it ("Our hand-dyed indigo
        // kaftan is back. Free size, soft cotton, ₦45,000" names the kaftan, not the cotton).
        var sentences = SentenceEnd().Split(candidate).Where(sentence => sentence.Trim().Length > 0).ToArray();
        // A question opens a post but doesn't name anything: "Planning an event? We create custom adire packs…"
        if (sentences.Where(sentence => !sentence.TrimEnd().EndsWith('?')).ToArray() is { Length: > 0 } statements) sentences = statements;
        var nearPrice = sentences.Length > 1 && tokens.FirstOrDefault()?.Line == first ? sentences[^1].Trim() : null;
        candidate = sentences.Length == 0 ? string.Empty
            : nearPrice is not null && !nearPrice.Contains(',') && nearPrice.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6
              && !Fluff().IsMatch(nearPrice) && !NotAName().IsMatch(nearPrice) ? nearPrice
            : sentences[0];
        candidate = Possessive().Replace(candidate, string.Empty);
        // "We create custom adire packs for teams" names the packs.
        if (WeMake().Match(candidate) is { Success: true } made) candidate = made.Groups["what"].Value;
        candidate = Trailing().Replace(candidate, string.Empty);
        candidate = Spaces().Replace(candidate, " ").Trim(' ', ':', '-', '–', ',', '.', '!', '|', '(', ')');
        // A capital to start, except for a name written that way on purpose ("iPhone", "eBay").
        if (candidate.Length > 0 && char.IsLower(candidate[0]) && !(candidate.Length > 1 && char.IsUpper(candidate[1]))) candidate = char.ToUpperInvariant(candidate[0]) + candidate[1..];
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 8) return words.Length > 8 ? Title(string.Join(' ', words.Take(6))) : string.Empty;
        if (Fluff().IsMatch(candidate) && words.Length > 3) return string.Empty;
        return candidate == candidate.ToUpperInvariant() ? Title(candidate) : candidate;
    }

    private static CatalogKind? Kind(string text)
    {
        if (DigitalWords().IsMatch(text)) return CatalogKind.Digital;
        if (ServiceWords().IsMatch(text)) return CatalogKind.Service;
        if (FoodWords().IsMatch(text)) return CatalogKind.Food;
        return CatalogKind.Product;
    }

    // ── Sentences ──────────────────────────────────────────────────────────

    // A bracketed aside is its own sentence: "9,000 naira / 1 yard (Minimum is 2 yards)" keeps the minimum.
    private static IEnumerable<string> Sentences(IEnumerable<string> lines) => lines
        .Select(line => line.Replace('(', '\n').Replace(')', '\n'))
        .SelectMany(line => line.Split('\n'))
        .SelectMany(line => SentenceEnd().Split(line))
        .Select(sentence => sentence.Trim(' ', '-', '*', '•', ','))
        .Where(sentence => sentence.Length >= 10);

    private static string Sentence(string text)
    {
        var sentence = text.Trim();
        if (sentence == sentence.ToUpperInvariant() && sentence.Any(char.IsLetter)) sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..].ToLowerInvariant();
        else sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..];
        return ".!?…".Contains(sentence[^1]) ? sentence : sentence + ".";
    }

    // "silver, gold , black Price : ₦15,500": the list ends where the next thing starts.
    private static string CutList(string list) => ListEnd().Split(list)[0];

    private static IEnumerable<string> SplitList(string list) =>
        ListSeparators().Split(list).Select(value => value.Trim(' ', '.', ':', '-')).Where(value => value.Length is > 0 and <= 30);

    private static string Title(string text) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Trim().ToLowerInvariant());

    private static string Join(string a, string b) => string.Join(' ', new[] { a, b }.Where(part => part.Length > 0));

    // Emoji and other symbols, joiners and hashtags (but not "#8,500", a price).
    private static string Clean(string text)
    {
        var cleaned = Hashtag().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), " ");
        cleaned = Symbols().Replace(cleaned, " ");
        return cleaned;
    }

    // ── Patterns ───────────────────────────────────────────────────────────

    private const string ColourWords = "black|white|grey|gray|red|blue|green|pink|purple|brown|beige|cream|gold|silver|nude|navy|wine|yellow|orange|chestnut|burgundy|indigo|rust|ash|champagne|lilac|mint|olive|peach|khaki|maroon|teal|coffee|tan|emerald|ivory|mustard|lemon|sky blue|royal blue|baby pink|off white";

    [GeneratedRegex(@"(?<from>\b(?:from|starts?\s+(?:from|at)|starting\s+(?:from|at)?)\s*)?(?<cur>₦|NGN|(?<![A-Za-z])N(?=\s?\d)|#(?=\s?\d))?\s?(?<num>\d{1,3}(?:,\s?\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)(?<mult>\s?[kKmM](?![A-Za-z]))?(?<naira>\s?(?:naira|ngn)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();

    [GeneratedRegex(@"^\s*(?:ml|cl|l\b|ltr|litres?|liters?|inch(?:es)?|""|”|in\b|gb|tb|kg|g\b|grams?|yards?|yds?|hrs?|hours?|days?|weeks?|months?|mins?|minutes?|pcs|pieces?|people|persons?|plates?|packs?|x\s?\d|by\s?\d|%|years?|yrs?|cm|mm|ft|feet|bedrooms?|strips?|looks?|outfits?|images?|pictures?|wraps?|cupcakes?|layers?|tiers?|[–\-]\s*\d{1,3}\b(?![,\d]))", RegexOptions.IgnoreCase)]
    private static partial Regex MeasureAfter();

    [GeneratedRegex(@"^\s*(?:only\s*)?(?:/|per|for|a)\s*(?<count>\d+)?\s*(?<unit>yards?|yds?|persons?|people|pax|hours?|hrs?|hr|plates?|packs?|bundles?|pieces?|pcs|pc|ml|litres?|liters?|nights?|days?|weeks?|months?|sessions?|classes?|heads?|each)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitAfter();

    [GeneratedRegex(@"\bper\s+(?<unit>yard|person|hour|plate|pack|bundle|piece|night|day|week|month|session|head)\b[\s:\-–]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitBefore();

    [GeneratedRegex(@"(?:(?:UK|US)\s*)?(?:size\s*)?\d+(?:\.\d+)?\s*(?:[–\-]\s*\d+(?:\.\d+)?)?\s*(?:inch(?:es)?|""|”|ml|cl|l|ltr|litres?|gb|tb|kg|g|yards?|cm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MeasureAtEnd();

    [GeneratedRegex(@"\b(?:price\s*list|pricelist|prices?|amount|cost|only|now|just|@)\b|📌|🏷️", RegexOptions.IgnoreCase)]
    private static partial Regex LabelNoise();

    [GeneratedRegex(@"\bsizes?(?=\b|\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeWord();

    [GeneratedRegex(@"(\d)\s*[–\-—]\s*(\d)")]
    private static partial Regex DashBetweenNumbers();

    [GeneratedRegex(@"^\s*(?:introduc\w*|new(?:\s+arrivals?)?|just\s+in|now\s+available|available\s+now|restocked|back\s+in\s+stock|meet)\b[\s:\-–!]*", RegexOptions.IgnoreCase)]
    private static partial Regex Opening();

    // Words before a price that say how to buy it, not what it is: "Available now for ₦32,000", "Get yours at".
    [GeneratedRegex(@"^\s*(?:available|now|only|price|get|grab|shop|order|buy|yours|it'?s|going)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAName();

    [GeneratedRegex(@"^\s*(?:our|my|the|this|these|meet\s+our)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Possessive();

    [GeneratedRegex(@"^\s*we\s+(?:make|create|sell|offer|do|provide|bake|sew|design|supply)\s+(?<what>.+?)(?=\s+(?:for|to|in|at|with|from)\b|[,.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WeMake();

    [GeneratedRegex(@"\s+(?:(?:is|are)\s+(?:back|here|out|now\s+available|available|in\s+stock|now|each)|is|are|for|at|each)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Trailing();

    [GeneratedRegex(@"\bin\s+(?:this\s+|our\s+|a\s+)?(?:gorgeous\s+|beautiful\s+|lovely\s+|stunning\s+|classy\s+|rich\s+|elegant\s+)?(?<colour>(?:" + ColourWords + @")(?:\s+(?:" + ColourWords + @"))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InColour();

    [GeneratedRegex(@"\b(?:colou?rs?|shades?)\b(?:\s+available\s*[:\-–]?|\s*[:\-–])\s*(?<list>[A-Za-z][A-Za-z ,/&]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ListedColours();

    [GeneratedRegex(@"\bflavou?rs?\b(?:\s+available\s*[:\-–]?|\s*[:\-–])\s*(?<list>[A-Za-z][A-Za-z ,/&]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ListedFlavours();

    [GeneratedRegex(@"\s*\b(?:price|prices|production|sizes?|delivery|deliver|available|only|for|in\s+stock|we|order|kindly|please)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListEnd();

    [GeneratedRegex(@"\bsizes?\b\s*(?:available)?\s*[:\-–]\s*(?<list>[A-Za-z0-9][A-Za-z0-9 ,/&–\-—]+?)\s*(?:[.\n]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ListedSizes();

    [GeneratedRegex(@"\s*(?:,|/|&|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparators();

    [GeneratedRegex(@"(?:production\s+time|ready\s+in|takes|it\s+takes|turnaround(?:\s+time)?|processing\s+time|delivered\s+in|lead\s+time)\s*[:\-]?\s*(?<time>\d+\s*(?:[–\-]\s*\d+\s*)?(?:working\s+|business\s+)?(?:hours?|hrs?|days?|weeks?|minutes?|mins?))", RegexOptions.IgnoreCase)]
    private static partial Regex LeadTime();

    [GeneratedRegex(@"\bpre-?\s?orders?\b|\bmade\s+to\s+order\b|\bcustom[\s-]?made\b|\bproduction\s+time\b|\bbespoke\b", RegexOptions.IgnoreCase)]
    private static partial Regex MadeToOrder();

    [GeneratedRegex(@"\bproduction\b|\bmade\b|\bpre-?\s?order", RegexOptions.IgnoreCase)]
    private static partial Regex Production();

    [GeneratedRegex(@"\b(?:dm|inbox|message|contact|call|ask|send\s+a\s+dm)\b[^.\n]{0,20}\bprice\b|\bprice\b[^.\n]{0,20}\b(?:on\s+request|in\s+(?:the\s+)?dm|dm|varies)\b|\bP\.?O\.?R\b", RegexOptions.IgnoreCase)]
    private static partial Regex AskForPrice();

    [GeneratedRegex(@"\b(?:deliver\w*|shipping|ship|dispatch\w*|waybill|payment|pay\b|transfer|whatsapp|dm\b|dms\b|inbox|link\s+in\s+(?:our\s+|my\s+)?bio|order\s+(?:via|on|through|directly)|website|www\.|\.com\b|discount|%\s*off|\bcode\b|nationwide|send\s+(?:a|us)\s+(?:dm|message)|place\s+(?:an\s+)?order|refund|exchange)", RegexOptions.IgnoreCase)]
    private static partial Regex Business();

    [GeneratedRegex(@"\b(?:love|gorgeous|beautiful|stunning|lovely|don'?t\s+miss|grab\s+yours|shop\s+now|you\s+will|available\s+now|restock\w*|new\s+arrivals?|introducing|slay|queen|vibes?|hurry|limited)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Fluff();

    [GeneratedRegex(@"\b(?:ebook|e-book|course|template|preset|pdf|download|masterclass|webinar)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DigitalWords();

    [GeneratedRegex(@"\b(?:glam|makeup|make-up|braid\w*|class|lesson|session|booking|book\s+(?:a|your)|photoshoot|shoot|cleaning|laundry|tutor\w*|consult\w*|training|appointment|home\s+service|lash(?:es)?|nails?|pedicure|manicure|haircut|styling)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceWords();

    [GeneratedRegex(@"\b(?:rice|jollof|chops|chicken|meat|soup|stew|cake|cupcakes?|drink|juice|zobo|smoothie|parfait|tray|platter|cooler|bowl|shawarma|suya|pizza|burger|meal|food|pastr\w*|puff[\s-]?puff|chin\s*chin)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FoodWords();

    [GeneratedRegex(@"#(?=\p{L})\S+")]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"[\p{So}\p{Cs}\p{Sk}\u200D\uFE0F]")]
    private static partial Regex Symbols();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(?:brand\s+new|(?:uk|london|us|foreign|nigerian|naija|fairly|slightly)\s+used|tokunbo|open\s+box|refurbished)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionPart();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?(?:gb|tb)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StoragePart();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?(?:inch(?:es)?\b|""|”|cm\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LengthPart();

    [GeneratedRegex(@"\b(?:(?:uk|us|eu)\s?(?:size\s?)?\d+(?:\.\d+)?(?:\s?[–\-]\s?\d+(?:\.\d+)?)?|size\s?\d+(?:\.\d+)?|\d+(?:\.\d+)?\s?(?:ml|cl|ltr|litres?|liters?|kg|g)|xxs|xs|xxl|xl|[2-5]xl|small|medium|large|big|mini)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizePart();

    [GeneratedRegex(@"\b(?:" + ColourWords + @")\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColourPart();

    [GeneratedRegex(@"^(?:xxs|xs|s|m|l|xl|xxl|[2-5]xl)$", RegexOptions.IgnoreCase)]
    private static partial Regex LetterSize();

    [GeneratedRegex(@"^(?:uk|us|eu)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Region();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$")]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"(?<=[.!?])\s+|\s*;\s*")]
    private static partial Regex SentenceEnd();
}
