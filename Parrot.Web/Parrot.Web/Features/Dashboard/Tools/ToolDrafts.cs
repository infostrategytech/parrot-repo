using System.Text.RegularExpressions;
using Parrot.Web.Features.Settings;

namespace Parrot.Web.Features.Dashboard.Tools;

// The choices being made in the connect window before they're saved. Each knows what still stops it finishing,
// so the window can say so instead of failing on save.

/// <summary>Calendly: which event types (services) the agent may offer. Everything found is on to start: the business
/// only switches off what the agent shouldn't offer (designer, 2 November 2026).</summary>
public sealed class CalendlyDraft
{
    public HashSet<string> Services { get; } = new(StringComparer.Ordinal);
    private IReadOnlyList<ToolResource> _found = [];

    public static CalendlyDraft From(ToolSetup setup, ToolSettings? saved)
    {
        var draft = new CalendlyDraft { _found = setup.Resources };
        var ids = setup.Resources.Select(resource => resource.Id).ToHashSet();
        if (saved?.Services is { } chosen) draft.Services.UnionWith(chosen.Select(service => service.Id).Where(ids.Contains));
        else draft.Services.UnionWith(setup.Resources.Select(resource => resource.Id));
        return draft;
    }

    public string? Problem => Services.Count == 0 ? "Choose at least one service your agent can book." : null;
    public ToolSettings ToSettings() => new(Services: [.. _found.Where(resource => Services.Contains(resource.Id))]);
}

/// <summary>Google Calendar: nothing to choose (designer, 2 November 2026). Bookings go to the main calendar, which
/// the agent also checks for busy times; without Google's permission to add events, the agent only offers times.</summary>
public sealed class CalendarDraft
{
    public bool CanBook { get; init; }
    public ToolResource? Main { get; init; }

    public static CalendarDraft From(ToolSetup setup) => new()
    {
        CanBook = setup.Granted.Contains("create-events"),
        Main = setup.Resources.FirstOrDefault(resource => resource.Main && !resource.ReadOnly) ?? setup.Resources.FirstOrDefault(resource => !resource.ReadOnly),
    };

    public string? Problem => CanBook && Main is null ? "This Google account has no calendar yára can add bookings to. Use a different account." : null;
    public ToolSettings ToSettings() => new(Calendar: new(CanBook ? Main : null));
}

/// <summary>A tab of a chosen sheet: on or off, and the columns the agent shouldn't see. A tab with no column names in
/// its first row can't be read, so it stays off.</summary>
public sealed class LookupTabChoice
{
    public required SheetTab Tab { get; init; }
    public bool On { get; set; } = true;
    public bool Readable => Tab.Headers.Count > 0;
    public HashSet<string> LeftOut { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Shown => [.. Tab.Headers.Where(header => !LeftOut.Contains(header))];
}

/// <summary>A sheet picked in Google's file picker, with its tabs.</summary>
public sealed class LookupSheetChoice
{
    public required SheetDetails Sheet { get; init; }
    public List<LookupTabChoice> Tabs { get; } = [];
}

/// <summary>Google Sheets: the sheets the agent looks things up in (designer, 2 November 2026). A sheet picked is on
/// in full, except columns that look private, which start left out; the business turns off what the agent
/// shouldn't use.</summary>
public sealed class SheetDraft
{
    public List<LookupSheetChoice> Sheets { get; } = [];

    // Words in a header that usually mean it's for the business only: notes, contacts, costs and margins.
    private static readonly string[] PrivateWords = ["internal", "note", "notes", "private", "phone", "email", "supplier", "margin", "profit", "comment", "comments", "password"];
    private static readonly string[] PrivatePhrases = ["cost price", "buying price", "unit cost", "bought at"];

    /// <summary>A header that looks like it's for the business only, so it starts left out.</summary>
    public static bool LooksPrivate(string header)
    {
        var text = header.Trim().ToLowerInvariant();
        return PrivatePhrases.Any(text.Contains) || Regex.Split(text, "[^\\p{L}]+").Any(word => PrivateWords.Contains(word));
    }

    /// <summary>The sheets already chosen, as they are in Google now: tabs that no longer exist drop out, and new
    /// tabs start off, so nothing new reaches the agent without being turned on.</summary>
    public static SheetDraft From(SheetSettings? saved, IEnumerable<SheetDetails> found)
    {
        var draft = new SheetDraft();
        foreach (var kept in saved?.Sheets ?? [])
        {
            if (found.FirstOrDefault(sheet => sheet.Id == kept.SpreadsheetId) is not { } details) continue;
            var choice = new LookupSheetChoice { Sheet = details };
            foreach (var tab in details.Tabs)
            {
                var savedTab = kept.Tabs.FirstOrDefault(item => item.Name == tab.Name);
                var item = new LookupTabChoice { Tab = tab, On = savedTab is not null && tab.Headers.Count > 0 };
                item.LeftOut.UnionWith(savedTab?.LeftOut.Where(tab.Headers.Contains) ?? tab.Headers.Where(LooksPrivate));
                choice.Tabs.Add(item);
            }
            draft.Sheets.Add(choice);
        }
        return draft;
    }

    /// <summary>A sheet just picked: every tab on, private-looking columns left out. Picking it again changes nothing.</summary>
    public void Add(SheetDetails details)
    {
        if (Sheets.Any(sheet => sheet.Sheet.Id == details.Id)) return;
        var choice = new LookupSheetChoice { Sheet = details };
        foreach (var tab in details.Tabs)
        {
            var item = new LookupTabChoice { Tab = tab, On = tab.Headers.Count > 0 };
            item.LeftOut.UnionWith(tab.Headers.Where(LooksPrivate));
            choice.Tabs.Add(item);
        }
        Sheets.Add(choice);
    }

    public void Remove(string spreadsheetId) => Sheets.RemoveAll(sheet => sheet.Sheet.Id == spreadsheetId);

    public string? Problem
    {
        get
        {
            if (Sheets.Count == 0) return "Choose a sheet from Google Drive.";
            if (Sheets.FirstOrDefault(sheet => !sheet.Tabs.Any(tab => tab.On)) is { } empty)
                return empty.Tabs.Any(tab => tab.Readable) ? $"Turn on a tab in {empty.Sheet.Name}, or remove it." : $"{empty.Sheet.Name} has no tab your agent can read. Add column names in Google Sheets, or remove it.";
            if (Sheets.SelectMany(sheet => sheet.Tabs).FirstOrDefault(tab => tab.On && tab.Shown.Count == 0) is { } blank) return $"Show at least one column in {blank.Tab.Name}.";
            return null;
        }
    }

    public ToolSettings ToSettings() => new(Sheet: new([.. Sheets.Select(sheet => new LookupSheet(sheet.Sheet.Id, sheet.Sheet.Name,
        [.. sheet.Tabs.Where(tab => tab.On).Select(tab => new LookupTab(tab.Tab.Name, [.. tab.Tab.Headers.Where(tab.LeftOut.Contains)]))]))]));
}

/// <summary>Opening hours in plain words, as customers are offered them: "Mon–Fri, 9am–6pm · Sat, 9am–4pm".</summary>
public static class BookingFormat
{
    public static IReadOnlyList<DayOfWeek> Week { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string Day(DayOfWeek day) => day.ToString()[..3];
    public static string Time(TimeOnly time) => (time.Hour % 12 == 0 ? 12 : time.Hour % 12) + (time.Minute == 0 ? "" : $":{time.Minute:00}") + (time.Hour < 12 ? "am" : "pm");

    /// <summary>"Mon–Sat", "Mon, Wed, Fri" or "Every day".</summary>
    public static string Days(IEnumerable<DayOfWeek> days)
    {
        var chosen = Week.Select(day => days.Contains(day)).ToArray();
        if (chosen.All(on => on)) return "Every day";
        var runs = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if (!chosen[i]) continue;
            var end = i;
            while (end + 1 < 7 && chosen[end + 1]) end++;
            runs.Add(end - i >= 2 ? $"{Day(Week[i])}–{Day(Week[end])}" : end > i ? $"{Day(Week[i])}, {Day(Week[end])}" : Day(Week[i]));
            i = end;
        }
        return string.Join(", ", runs);
    }

    /// <summary>The open days, with days that share the same hours run together; null when no day is open.</summary>
    public static string? OpeningHours(IEnumerable<DayHours> hours)
    {
        var open = Week.Select(day => hours.FirstOrDefault(item => item.Day == day && item.Open && item.Closes > item.Opens)).ToArray();
        var parts = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if (open[i] is not { } first) continue;
            var end = i;
            while (end + 1 < 7 && open[end + 1] is { } next && next.Opens == first.Opens && next.Closes == first.Closes) end++;
            parts.Add($"{Days(Week.Skip(i).Take(end - i + 1))}, {Time(first.Opens)}–{Time(first.Closes)}");
            i = end;
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
