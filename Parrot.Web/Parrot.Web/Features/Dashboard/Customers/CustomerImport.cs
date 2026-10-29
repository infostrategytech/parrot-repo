using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Dashboard.Customers;

/// <summary>One customer as read from a list, before it is checked.</summary>
/// <param name="NeedsCheck">Read with little confidence (a photo can be misread): the review asks the owner to look.</param>
public sealed record ImportEntry(string Name, string? Phone, string? Email, bool NeedsCheck = false);

/// <summary>A number as typed, made one shape (+ and digits), or what's wrong with it: a short Problem for a
/// list, and Advice, the sentence under a field (ContactText, shared by every screen).</summary>
/// <param name="Local">Written as a local number (0803…): the country code was added.</param>
public sealed record PhoneReading(string? Number, string? Problem, bool Local = false, string? Advice = null);

/// <summary>
/// Bringing in the customers a business already has (designer, 22 October 2026: most small businesses
/// don't know what a CSV is). A spreadsheet (Excel or CSV), a phone's contacts file (.vcf) or a pasted list
/// is read here, by the app itself, with no other service; a photo needs a reading service
/// (<see cref="ICustomerListReader"/>). Nothing read is kept: only the customers the owner adds are saved.
/// Numbers can be written as the business writes them (0803…): local ones are taken to be Nigerian until the
/// workspace keeps its country (customers-backend-requests).
/// </summary>
public static partial class CustomerImport
{
    public const int MaxRows = 1000;
    public const long MaxTextBytes = 1024 * 1024;
    public const long MaxSheetBytes = 5 * 1024 * 1024;
    public const long MaxPhotoBytes = 10 * 1024 * 1024;

    /// <summary>The country a local number (0803…) belongs to, until the workspace keeps its own.</summary>
    public const string HomeCode = "234";

    /// <summary>The template: the three columns, with one example row to copy.</summary>
    public const string Template = "Name,Phone,Email\r\nAdaeze Okafor,0801 234 5678,adaeze@example.com\r\n";

    public static readonly string[] SheetExtensions = [".csv", ".xlsx", ".vcf", ".txt"];
    public static readonly string[] PhotoTypes = ["image/jpeg", "image/png", "image/webp", "image/heic"];

    // ── Numbers and emails ──────────────────────────────────────────────

    /// <summary>
    /// "0803 555 1234", "803-555-1234", "234 803 555 1234" and "+234 803 555 1234" all become +2348035551234.
    /// </summary>
    public static PhoneReading Phone(string raw)
    {
        var check = ContactText.Phone(raw);
        return new(check.Number, check.Problem, check.Local, check.Advice);
    }

    /// <summary>A number as it's easy to read: +234 803 555 1234.</summary>
    public static string Display(string number) => ContactText.Display(number);

    public static bool IsEmail(string raw) => ContactText.IsEmail(raw);

    // ── Files ───────────────────────────────────────────────────────────

    /// <summary>A spreadsheet (.xlsx or .csv), a contacts file (.vcf) or a text file of customers.</summary>
    /// <returns>The customers in it, or what's wrong with the file as a whole.</returns>
    public static (IReadOnlyList<ImportEntry> Entries, string? Problem) ReadFile(Stream file, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var entries = extension switch
            {
                ".xlsx" => FromGrid(ReadXlsx(file)),
                ".vcf" => FromContacts(ReadAll(file)),
                ".txt" => ReadText(ReadAll(file)),
                _ => FromGrid(Grid(ReadAll(file))),
            };
            return Checked(entries, extension == ".vcf" ? "contacts in that file" : "customers in that file");
        }
        catch (InvalidDataException)
        {
            return ([], extension == ".xlsx"
                ? "That workbook couldn't be read. Open it in Excel and save it again, or paste the list."
                : "That file couldn't be read. Try saving it again, or paste the list.");
        }
    }

    /// <summary>
    /// A pasted list, in any layout: one customer a line ("Ada Obi 0803 555 1234", "Kemi's Kitchen, 0805…,
    /// kemi@example.com"), a name on one line and its number on the next, or rows copied from a spreadsheet.
    /// </summary>
    public static List<ImportEntry> ReadText(string text)
    {
        text = text.TrimStart('﻿');
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToList();
        if (lines.Count == 0) return [];
        // Rows copied from a spreadsheet arrive with tabs; a CSV with its header row.
        if (lines.Count(line => line.Contains('\t')) * 2 > lines.Count || LooksLikeHeader(Split(lines[0], ',')))
            return FromGrid(Grid(text));

        var entries = new List<ImportEntry>();
        foreach (var raw in lines)
        {
            var line = Bullet().Replace(raw.Trim(), "");
            var email = EmailInText().Match(line) is { Success: true } e ? e.Value : null;
            if (email is not null) line = line.Replace(email, " ");
            var phone = PhoneInText().Matches(line).FirstOrDefault(m => m.Value.Count(char.IsDigit) >= 8)?.Value;
            if (phone is not null) line = line.Replace(phone, " ");
            var name = Labels().Replace(line, " ");
            name = Spaces().Replace(name.Trim().Trim('-', '–', '—', ':', ',', ';', '|', '/', '.', ' '), " ").Trim();

            // A number or an email on a line of its own belongs to the name above it.
            if (name.Length == 0 && entries.Count > 0 && entries[^1] is var last
                && (phone is null || last.Phone is null) && (email is null || last.Email is null))
            {
                entries[^1] = last with { Phone = last.Phone ?? phone, Email = last.Email ?? email };
                continue;
            }
            if (name.Length == 0 && phone is null && email is null) continue;
            entries.Add(new ImportEntry(name, phone, email));
            if (entries.Count > MaxRows) break;
        }
        return entries;
    }

    /// <summary>Too many, or none: said for the whole list.</summary>
    public static (IReadOnlyList<ImportEntry> Entries, string? Problem) Checked(List<ImportEntry> entries, string what = "customers in that list") =>
        entries.Count == 0 ? ([], $"There are no {what}. Check it has names or numbers in it.")
        : entries.Count > MaxRows ? ([], $"That's more than {MaxRows:N0} customers. Split it and import it in parts.")
        : (entries, null);

    private static string ReadAll(Stream file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new InvalidDataException("Not a text file.");
        return text.TrimStart('﻿');
    }

    // ── Spreadsheets: a grid of cells, then which columns are which ─────

    private static readonly Regex NameHeader = new(@"^(?:name|names|full\s*name|customer|customer\s*name|client|contact|contact\s*name|display\s*name)$", RegexOptions.IgnoreCase);
    private static readonly Regex FirstNameHeader = new(@"^(?:first\s*name|given\s*name|firstname)$", RegexOptions.IgnoreCase);
    private static readonly Regex LastNameHeader = new(@"^(?:last\s*name|surname|family\s*name|lastname)$", RegexOptions.IgnoreCase);
    private static readonly Regex PhoneHeader = new(@"(?:phone|mobile|whats\s*app|tel|telephone|number|cell|gsm)", RegexOptions.IgnoreCase);
    private static readonly Regex EmailHeader = new(@"(?:e-?mail)", RegexOptions.IgnoreCase);

    private static bool LooksLikeHeader(IReadOnlyList<string> row) =>
        row.Any(cell => NameHeader.IsMatch(cell.Trim()) || FirstNameHeader.IsMatch(cell.Trim()))
        && row.Any(cell => PhoneHeader.IsMatch(cell) || EmailHeader.IsMatch(cell))
        && row.All(cell => !PhoneInText().IsMatch(cell) && !EmailInText().IsMatch(cell));

    private static List<ImportEntry> FromGrid(List<List<string>> grid)
    {
        var rows = grid.Where(row => row.Any(cell => cell.Trim().Length > 0)).ToList();
        if (rows.Count == 0) return [];
        var width = rows.Max(row => row.Count);
        string Cell(List<string> row, int column) => column >= 0 && column < row.Count ? row[column].Trim() : "";

        int name = -1, first = -1, last = -1, phone = -1, email = -1;
        var body = rows;
        var header = Enumerable.Range(0, width).Select(column => Cell(rows[0], column)).ToList();
        if (LooksLikeHeader(header))
        {
            name = header.FindIndex(cell => NameHeader.IsMatch(cell));
            first = header.FindIndex(cell => FirstNameHeader.IsMatch(cell));
            last = header.FindIndex(cell => LastNameHeader.IsMatch(cell));
            phone = header.FindIndex(cell => PhoneHeader.IsMatch(cell) && !EmailHeader.IsMatch(cell));
            email = header.FindIndex(cell => EmailHeader.IsMatch(cell));
            body = rows.Skip(1).ToList();
        }
        else
        {
            // No header: the column mostly of numbers, the one mostly of emails, and the first mostly of words.
            bool Mostly(int column, Func<string, bool> test) => body.Count(row => test(Cell(row, column))) * 2 > body.Count;
            phone = Enumerable.Range(0, width).FirstOrDefault(column => Mostly(column, cell => cell.Count(char.IsDigit) >= 8 && cell.Count(char.IsLetter) == 0), -1);
            email = Enumerable.Range(0, width).FirstOrDefault(column => Mostly(column, IsEmail), -1);
            name = Enumerable.Range(0, width).Where(column => column != phone && column != email)
                .FirstOrDefault(column => Mostly(column, cell => cell.Any(char.IsLetter)), -1);
        }

        return body.Take(MaxRows + 1).Select(row =>
        {
            var fullName = name >= 0 ? Cell(row, name) : $"{Cell(row, first)} {Cell(row, last)}".Trim();
            var number = Cell(row, phone);
            var address = Cell(row, email);
            return new ImportEntry(fullName, number.Length > 0 ? number : null, address.Length > 0 ? address : null);
        }).Where(entry => entry.Name.Length > 0 || entry.Phone is not null || entry.Email is not null).ToList();
    }

    // CSV (commas or semicolons) or rows copied from a spreadsheet (tabs), with quoted cells.
    private static List<List<string>> Grid(string text)
    {
        var separator = text.Count(c => c == '\t') > 0 ? '\t' : text.Count(c => c == ';') > text.Count(c => c == ',') ? ';' : ',';
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
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == separator) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = [];
                if (rows.Count > MaxRows + 2) break;
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static List<string> Split(string line, char separator) => [.. line.Split(separator).Select(cell => cell.Trim().Trim('"'))];

    // The first sheet of an Excel workbook, read from its XML.
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static List<List<string>> ReadXlsx(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            // Some tools write the paths inside with backslashes.
            XDocument? Load(string path) => (zip.GetEntry(path) ?? zip.GetEntry(path.Replace('/', '\\'))) is { } entry ? XDocument.Load(entry.Open()) : null;

            var shared = Load("xl/sharedStrings.xml")?.Root?.Elements(Main + "si")
                .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList() ?? [];

            var sheetPath = "xl/worksheets/sheet1.xml";
            var firstSheet = Load("xl/workbook.xml")?.Root?.Element(Main + "sheets")?.Elements(Main + "sheet").FirstOrDefault();
            var relId = firstSheet?.Attribute(Rel + "id")?.Value;
            var target = Load("xl/_rels/workbook.xml.rels")?.Root?.Elements(PackageRel + "Relationship")
                .FirstOrDefault(r => r.Attribute("Id")?.Value == relId)?.Attribute("Target")?.Value;
            if (target is { Length: > 0 }) sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var sheet = Load(sheetPath) ?? throw new InvalidDataException("No sheet found.");
            var rows = new List<List<string>>();
            foreach (var row in sheet.Descendants(Main + "row").Take(MaxRows + 2))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = Column(cell.Attribute("r")?.Value ?? "", cells.Count);
                    while (cells.Count < column) cells.Add("");
                    var value = cell.Element(Main + "v")?.Value ?? "";
                    var text = cell.Attribute("t")?.Value switch
                    {
                        "s" when int.TryParse(value, out var index) && index < shared.Count => shared[index],
                        "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
                        // A number Excel kept as a number (2348035551234, or 8035551234 once its 0 was dropped).
                        _ => value.EndsWith(".0") ? value[..^2] : value,
                    };
                    if (column < cells.Count) cells[column] = text; else cells.Add(text);
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidDataException or IOException)
        {
            throw new InvalidDataException("The workbook couldn't be read.", exception);
        }
    }

    // "C12" → 2 (zero-based); a cell with no reference follows the last.
    private static int Column(string reference, int next)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length == 0 ? next : letters.Aggregate(0, (sum, letter) => sum * 26 + (letter - 'A' + 1)) - 1;
    }

    // ── Contacts files (.vcf): what a phone exports or shares ───────────

    private static List<ImportEntry> FromContacts(string text)
    {
        // Long lines are folded onto the next, starting with a space; quoted-printable lines end with "=".
        var unfolded = Regex.Replace(text.Replace("\r\n", "\n"), @"\n[ \t]", "");
        unfolded = Regex.Replace(unfolded, @"=\n", "");
        var entries = new List<ImportEntry>();
        string? name = null, family = null, given = null, email = null;
        var phones = new List<(string Number, bool Mobile)>();
        foreach (var raw in unfolded.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            var property = key.Split(';')[0].Split('.').Last().ToUpperInvariant();
            if (key.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)) value = QuotedPrintable(value);
            switch (property)
            {
                case "BEGIN": name = family = given = email = null; phones.Clear(); break;
                case "FN": name = value.Replace("\\,", ",").Trim(); break;
                case "N":
                    var parts = value.Split(';');
                    family = parts.ElementAtOrDefault(0)?.Trim();
                    given = parts.ElementAtOrDefault(1)?.Trim();
                    break;
                case "TEL": phones.Add((value, Regex.IsMatch(key, "CELL|MOBILE", RegexOptions.IgnoreCase))); break;
                case "EMAIL": email ??= value; break;
                case "END":
                    var fullName = name is { Length: > 0 } ? name : $"{given} {family}".Trim();
                    var phone = phones.OrderByDescending(p => p.Mobile).Select(p => p.Number).FirstOrDefault();
                    if (fullName.Length > 0 || phone is not null) entries.Add(new ImportEntry(fullName, phone, email));
                    if (entries.Count > MaxRows) return entries;
                    break;
            }
        }
        return entries;
    }

    private static string QuotedPrintable(string value)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex Separators();


    [GeneratedRegex(@"[^\s@,;<>()]+@[^\s@,;<>()]+\.[^\s@,;<>()]+")]
    private static partial Regex EmailInText();

    [GeneratedRegex(@"\+?\d[\d \-().]{6,}\d")]
    private static partial Regex PhoneInText();

    // "1.", "2)", "-", "•" at the start of a line.
    [GeneratedRegex(@"^(?:\d{1,3}[.)]\s+|[-•*·]\s*)")]
    private static partial Regex Bullet();

    // "Name:", "Phone -", "Tel", "WhatsApp:" and the like, which aren't part of the name.
    [GeneratedRegex(@"\b(?:name|phone(?:\s*number)?|tel|mobile|whats\s*app|number|email|e-mail)\b\s*[:=\-]?", RegexOptions.IgnoreCase)]
    private static partial Regex Labels();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}

/// <summary>
/// One customer being reviewed before the import: what was read, as the owner corrects it. The checks run
/// across the whole list (<see cref="ImportReview.Problems"/>), as two rows can share a number.
/// </summary>
public sealed class ImportDraft(int id, ImportEntry entry)
{
    public int Id { get; } = id;
    public string Name { get; set; } = entry.Name;
    public string Phone { get; set; } = entry.Phone ?? "";
    public string Email { get; set; } = entry.Email ?? "";

    /// <summary>Hard to read: stays a question until the owner says it's right or changes it.</summary>
    public bool NeedsCheck { get; set; } = entry.NeedsCheck;

    public string? Number => CustomerImport.Phone(Phone).Number;
    public string? Address => Email.Trim().Length > 0 && CustomerImport.IsEmail(Email) ? Email.Trim() : null;
}

public static class ImportReview
{
    /// <summary>
    /// What needs a look on each row, by id: no name, a number or email that can't be right, someone already a
    /// customer (<paramref name="knownPhones"/>, digits only), the same number or email twice, or a number a photo
    /// made hard to read. A row with none is ready to add.
    /// </summary>
    public static Dictionary<int, List<string>> Problems(IReadOnlyList<ImportDraft> drafts, IReadOnlySet<string> knownPhones)
    {
        var problems = drafts.ToDictionary(draft => draft.Id, _ => new List<string>());
        var phones = new Dictionary<string, ImportDraft>();
        var emails = new Dictionary<string, ImportDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            var list = problems[draft.Id];
            if (draft.Name.Trim().Length == 0) list.Add("Add their name");
            else if (draft.Name.Trim().Length > 256) list.Add("Name is too long");

            var reading = CustomerImport.Phone(draft.Phone);
            if (reading.Problem is { } problem) list.Add(problem);
            else if (reading.Number is { } number)
            {
                var key = number.TrimStart('+');
                if (knownPhones.Contains(key)) list.Add("Already one of your customers");
                else if (phones.TryGetValue(key, out var first)) list.Add(first.Name.Trim().Length > 0 ? $"Same number as {first.Name.Trim()}" : "Same number as another row");
                else phones[key] = draft;
                if (draft.NeedsCheck) list.Add("Hard to read: check the number");
            }

            var email = draft.Email.Trim();
            if (email.Length > 0)
            {
                if (!CustomerImport.IsEmail(email)) list.Add("Check this email");
                else if (emails.TryGetValue(email, out var first)) list.Add(first.Name.Trim().Length > 0 ? $"Same email as {first.Name.Trim()}" : "Same email as another row");
                else emails[email] = draft;
            }
        }
        return problems;
    }
}
