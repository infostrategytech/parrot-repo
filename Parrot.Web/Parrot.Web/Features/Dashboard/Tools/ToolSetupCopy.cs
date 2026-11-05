namespace Parrot.Web.Features.Dashboard.Tools;

/// <summary>One line of the provider's own permission screen, in the provider's words. Key: what it allows
/// (an action id). Required: yára can't work without it.</summary>
public sealed record ProviderPermission(string Key, string Text, bool Required = false);

/// <summary>
/// Whose sign-in a tool uses (Google for both Google tools), its button, and what its permission screen asks
/// (researched 1 November 2026). Google shows a checkbox per permission when it asks for more than one, and people
/// can untick them; Calendly and a single Google permission are allowed or not as a whole.
/// </summary>
public sealed record ToolSetupWords(string Provider, string Cta, bool Google, IReadOnlyList<ProviderPermission> Permissions)
{
    public bool Tickable => Google && Permissions.Count > 1;
}

public static class ToolSetupCopy
{
    public static ToolSetupWords For(string toolId) => toolId switch
    {
        "accelerate" => new("Accelerate", "Connect", false, []),
        "calendly" => new("Calendly", "Allow", false,
            [new("check-availability", "See your profile, event types and available times", true),
             new("create-bookings", "Book meetings and create booking links")]),
        "google-calendar" => new("Google", "Continue", true,
            // Two permissions, not three: yára uses the main calendar, so it never needs the calendar list.
            [new("check-availability", "View your availability in your calendars", true),
             new("create-events", "See, create, change and delete events on Google calendars you own")]),
        _ => new("Google", "Continue", true,
            [new("find-information", "See, edit, create and delete only the specific Google Drive files you use with this app", true)]),
    };
}

/// <summary>
/// The demo's stand-ins for the provider's own windows. For real, the provider answers the backend's callback and
/// yára only reads the result (CheckAuthorizationAsync), and Google's own file picker returns the chosen file's id.
/// </summary>
public interface IToolProviderPreview
{
    /// <summary>The account the provider's window shows as signed in.</summary>
    string SignedInAs(Guid setupId);

    /// <summary>"Use a different account": for real, the provider's own account chooser.</summary>
    void UseAnotherAccount(Guid setupId);

    /// <summary>What the window answered: the permissions allowed, or null when it was closed early.</summary>
    Task AnswerAsync(Guid setupId, IReadOnlyList<string>? allowed, CancellationToken ct = default);

    /// <summary>The spreadsheets Google's file picker would list.</summary>
    IReadOnlyList<ToolResource> PickerFiles(Guid setupId);
}
