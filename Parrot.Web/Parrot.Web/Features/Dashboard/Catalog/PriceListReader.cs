using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>One item read from a price list, to check before it is added.</summary>
/// <param name="NeedsCheck">Read with little confidence (a photo can be misread): the review asks the owner to look.</param>
public sealed record PriceListLine(string Name, decimal? Price, bool From, CatalogKind Kind, bool NeedsCheck = false, string? CurrencyCode = null);

/// <summary>
/// Reading a photo of a price list (printed, handwritten, a menu or a
/// screenshot of a spreadsheet) into items. It needs a reading service (text
/// recognition with AI); the screens offer "Snap a photo" only where one
/// exists, as Channels offers only what can connect.
/// </summary>
public interface IPriceListReader
{
    bool CanReadPhotos { get; }

    /// <summary>The items on the photo, in the order they appear; an empty list when none could be read.</summary>
    Task<IReadOnlyList<PriceListLine>> ReadPhotoAsync(Stream photo, string contentType, CancellationToken cancellationToken = default);
}

/// <summary>Real mode today: no reading service yet (see catalog-backend-requests).</summary>
public sealed class UnavailablePriceListReader : IPriceListReader
{
    public bool CanReadPhotos => false;

    public Task<IReadOnlyList<PriceListLine>> ReadPhotoAsync(Stream photo, string contentType, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Reading photos needs a reading service.");
}

/// <summary>
/// Price lists as people keep them: text pasted from a WhatsApp chat, a CSV
/// file, or an Excel workbook. Read by the app itself, with no other service,
/// and not kept: only the items the owner adds are saved.
/// </summary>
public static class PriceListParser
{
    public const int MaxLines = 500;

    // "Name - 45,000", "Name: ₦45,000", "Name 12k", "Name from 25000".
    private static readonly Regex Line = new(@"^(?<name>.+?)[\s\-–—:,|=]*(?<from>from\s+)?(?:₦|\$|£|€|GH₵|NGN|USD|GBP|GHS|EUR)?\s*(?<amount>\d[\d,]*(?:\.\d+)?)\s*(?<k>k)?\s*(?:naira|NGN|USD|GBP|GHS|EUR)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Words that usually mean a service, or food, so the kind starts right.
    private static readonly Regex ServiceWords = new(@"\b(?:class|lesson|session|service|repair|fixing|braid\w*|hair|makeup|tailor\w*|sewing|consult\w*|training|booking|test|scan|check-?up)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FoodWords = new(@"\b(?:rice|jollof|chops|chicken|meat|soup|stew|cake|drink|juice|zobo|pepper|plate|bread|shawarma|suya|pizza|burger)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CatalogKind GuessKind(string name) =>
        ServiceWords.IsMatch(name) ? CatalogKind.Service : FoodWords.IsMatch(name) ? CatalogKind.Food : CatalogKind.Product;

    /// <summary>Text, one item a line: the WhatsApp-chat case.</summary>
    public static List<PriceListLine> ParseText(string text)
    {
        var lines = new List<PriceListLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '•', '*', ' ').Trim();
            if (line.Length < 2) continue;
            var match = Line.Match(line);
            var name = (match.Success ? match.Groups["name"].Value : line).Trim().TrimEnd('-', '–', ':', ',', ' ');
            if (name.Length == 0) continue;
            decimal? price = null;
            if (match.Success && NumberText.Decimal(match.Groups["amount"].Value) is { } amount)
                price = match.Groups["k"].Success ? amount * 1000 : amount;
            var mixed = CurrencyAmount.ExplicitCodes(line).Count > 1;
            lines.Add(new PriceListLine(Trim(name), !mixed && price is > 0 ? price : null, match.Success && match.Groups["from"].Success, GuessKind(name), NeedsCheck: mixed, CurrencyCode: CurrencyAmount.ExplicitCode(line)));
            if (lines.Count >= MaxLines) break;
        }
        return lines;
    }

    /// <summary>The first sheet of an Excel workbook (.xlsx), or a CSV file, with a guess at which columns hold the names and prices.</summary>
    /// <exception cref="InvalidDataException">The file isn't a workbook or CSV that can be read.</exception>
    public static PriceSheet ParseSpreadsheet(Stream file, string fileName) =>
        PriceSheet.From(fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(file) : ReadXlsx(file));

    // Best first: "Item name" over "Description", "Selling price" over "Cost".
    internal static readonly Regex[] NameHeaders =
    [
        new(@"\b(?:name|item|items|product|products|service|services|title|menu|test)\b", RegexOptions.IgnoreCase),
        new(@"\b(?:description|details)\b", RegexOptions.IgnoreCase),
    ];
    internal static readonly Regex[] PriceHeaders =
    [
        new(@"(?:selling|retail|sale\s*price)", RegexOptions.IgnoreCase),
        new(@"(?:price|amount|naira|₦|ngn|rate|fee)", RegexOptions.IgnoreCase),
        new(@"\bcost\b", RegexOptions.IgnoreCase),
    ];

    internal static int FindColumn(IReadOnlyList<string> header, Regex[] patterns, int except = -1)
    {
        foreach (var pattern in patterns)
            for (var column = 0; column < header.Count; column++)
                if (column != except && pattern.IsMatch(header[column])) return column;
        return -1;
    }

    /// <summary>An amount as written in a cell: "45000", "₦45,000", "NGN 45,000.00", "12k". Null for words.</summary>
    internal static decimal? Money(string value)
    {
        // Import may recognise explicit currency/shorthand, but must never strip arbitrary letters or a
        // negative sign. Manual fields use NumberText directly and do not accept k/m shortcuts.
        var match = Regex.Match(value.Trim(), @"\A(?:₦|\$|£|€|GH₵|(?:NGN|USD|GBP|GHS|EUR)\s*|N(?=\s*[0-9]))?\s*(?<number>[0-9.,]+)\s*(?<scale>[km])?\s*(?:naira|NGN|USD|GBP|GHS|EUR)?\z", RegexOptions.IgnoreCase);
        if (!match.Success || NumberText.Decimal(match.Groups["number"].Value) is not { } amount || amount <= 0) return null;
        return match.Groups["scale"].Value.ToLowerInvariant() switch { "k" => amount * 1000, "m" => amount * 1000000, _ => amount };
    }

    internal static string Trim(string name) => name.Length > 80 ? name[..80] : name;

    // ── CSV: quoted fields, commas or semicolons ────────────────────────────

    private static List<List<string>> ReadCsv(Stream file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new InvalidDataException("Not a text file.");
        var separator = text.Count(c => c == ';') > text.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = [];
                if (rows.Count > MaxLines + 1) break;
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ── Excel: the first sheet of an .xlsx, read from its XML ──────────────

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static List<List<string>> ReadXlsx(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            XDocument? Load(string path) => zip.GetEntry(path) is { } entry ? XDocument.Load(entry.Open()) : null;

            var shared = Load("xl/sharedStrings.xml")?.Root?.Elements(Main + "si")
                .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList() ?? [];

            // The first sheet the workbook lists, wherever its file is.
            var sheetPath = "xl/worksheets/sheet1.xml";
            var firstSheet = Load("xl/workbook.xml")?.Root?.Element(Main + "sheets")?.Elements(Main + "sheet").FirstOrDefault();
            var relId = firstSheet?.Attribute(Rel + "id")?.Value;
            var target = Load("xl/_rels/workbook.xml.rels")?.Root?.Elements(PackageRel + "Relationship")
                .FirstOrDefault(r => r.Attribute("Id")?.Value == relId)?.Attribute("Target")?.Value;
            if (target is { Length: > 0 }) sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var sheet = Load(sheetPath) ?? throw new InvalidDataException("No sheet found.");
            var rows = new List<List<string>>();
            foreach (var row in sheet.Descendants(Main + "row").Take(MaxLines + 1))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = Column(cell.Attribute("r")?.Value ?? string.Empty, cells.Count);
                    while (cells.Count < column) cells.Add(string.Empty);
                    var value = cell.Element(Main + "v")?.Value ?? string.Empty;
                    var text = cell.Attribute("t")?.Value switch
                    {
                        "s" when int.TryParse(value, out var index) && index < shared.Count => shared[index],
                        "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
                        _ => value,
                    };
                    if (column < cells.Count) cells[column] = text; else cells.Add(text);
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidDataException("The workbook couldn't be read.", exception);
        }
    }

    // "C12" → 2 (zero-based column); a cell with no reference follows the last.
    private static int Column(string reference, int next)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length == 0 ? next : letters.Aggregate(0, (sum, letter) => sum * 26 + (letter - 'A' + 1)) - 1;
    }
}

/// <summary>
/// A spreadsheet as read: its columns and rows, and which columns look like
/// the names and the prices. The owner can pick other columns; nothing else
/// in the sheet is used.
/// </summary>
public sealed class PriceSheet
{
    private PriceSheet(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, int nameColumn, int priceColumn)
    {
        Columns = columns;
        Rows = rows;
        NameColumn = nameColumn;
        PriceColumn = priceColumn;
    }

    /// <summary>What each column is called: its header, or "Column B (45000)" where there is none.</summary>
    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public int NameColumn { get; }

    /// <summary>-1 when no column looks like prices.</summary>
    public int PriceColumn { get; }

    public List<PriceListLine> Lines(int nameColumn, int priceColumn) => Rows
        .Select(row => (name: PriceListParser.Trim(Cell(row, nameColumn)), price: priceColumn >= 0 ? PriceListParser.Money(Cell(row, priceColumn)) : null,
            currency: priceColumn >= 0 ? CurrencyAmount.ExplicitCode(Cell(row, priceColumn)) ?? CurrencyAmount.ExplicitCode(Cell(Columns, priceColumn)) : null))
        .Where(line => line.name.Length > 0)
        .Take(PriceListParser.MaxLines)
        .Select(line => new PriceListLine(line.name, line.price, false, PriceListParser.GuessKind(line.name), CurrencyCode: line.currency))
        .ToList();

    internal static PriceSheet From(List<List<string>> grid)
    {
        var rows = grid
            .Where(row => row.Any(cell => cell.Trim().Length > 0))
            .Select(row => (IReadOnlyList<string>)row.Select(cell => cell.Trim()).ToList())
            .ToList();
        if (rows.Count == 0) return new([], [], 0, -1);
        var width = rows.Max(row => row.Count);
        var first = Enumerable.Range(0, width).Select(column => Cell(rows[0], column)).ToList();

        // A header row names a name or a price column, and holds no amounts
        // ("Malaria test | 5000" is an item, not a header).
        var looksLikeHeader = first.All(cell => PriceListParser.Money(cell) is null);
        var nameColumn = looksLikeHeader ? PriceListParser.FindColumn(first, PriceListParser.NameHeaders) : -1;
        var priceColumn = looksLikeHeader ? PriceListParser.FindColumn(first, PriceListParser.PriceHeaders, nameColumn) : -1;
        var hasHeader = nameColumn >= 0 || priceColumn >= 0;
        var body = hasHeader ? rows.Skip(1).ToList() : rows;

        // Without one: the first column mostly of words, the first mostly of amounts.
        bool Mostly(int column, Func<string, bool> test) => body.Count(row => test(Cell(row, column))) * 2 > body.Count;
        if (nameColumn < 0)
            nameColumn = Enumerable.Range(0, width).Where(column => column != priceColumn)
                .FirstOrDefault(column => Mostly(column, cell => cell.Length > 0 && PriceListParser.Money(cell) is null), 0);
        if (priceColumn < 0)
            priceColumn = Enumerable.Range(0, width).Where(column => column != nameColumn)
                .FirstOrDefault(column => Mostly(column, cell => PriceListParser.Money(cell) is not null), -1);

        var columns = Enumerable.Range(0, width).Select(column =>
        {
            if (hasHeader && first[column].Length > 0) return first[column];
            var example = body.Select(row => Cell(row, column)).FirstOrDefault(cell => cell.Length > 0);
            var label = $"Column {Letter(column)}";
            return example is null ? label : $"{label} ({(example.Length > 24 ? example[..24] + "…" : example)})";
        }).ToList();
        return new(columns, body, nameColumn, priceColumn);
    }

    private static string Cell(IReadOnlyList<string> row, int column) => column >= 0 && column < row.Count ? row[column] : string.Empty;

    // 0 → "A", 27 → "AB".
    private static string Letter(int column)
    {
        var letters = string.Empty;
        for (column++; column > 0; column = (column - 1) / 26) letters = (char)('A' + (column - 1) % 26) + letters;
        return letters;
    }
}
