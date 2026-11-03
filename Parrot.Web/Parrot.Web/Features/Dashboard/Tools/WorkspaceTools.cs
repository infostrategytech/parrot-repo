namespace Parrot.Web.Features.Dashboard.Tools;

public enum ToolConnectionStatus { SetupIncomplete, Connected, NeedsAttention }
public enum ToolActionMode { AskFirst, Automatic }

/// <summary>CanAskFirst: the owner may require approval for a payment or spreadsheet write. Worksheet-level
/// instructions and permissions narrow Sheets access further; bookings retain their existing rules.</summary>
public sealed record ToolAction(string Id, string Name, string Description, bool ChangesData, bool CanAskFirst = false);

/// <summary>How a business connects it: by signing in at the provider, or (Accelerate) with its API keys.</summary>
public enum ToolSignIn { Provider, Keys }

public sealed record ToolDefinition(string Id, string Name, string Category, string Headline, string Description,
    ToolSignIn SignIn, IReadOnlyList<ToolAction> Actions);

/// <summary>Product capabilities, not sample accounts or live provider promises. Integration is pending.
/// What each provider really allows was researched on 1 November 2026 (integrations-backend-requests).</summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        new("accelerate", "Accelerate", "Payments", "Get paid in the conversation",
            "Your agent sends a payment link for the agreed amount and tells you when it's paid.", ToolSignIn.Keys,
            [new("send-payment-links", "Send payment links", "Create a link for the agreed amount, and see when it's paid.", true, CanAskFirst: true)]),
        new("calendly", "Calendly", "Bookings", "Make room for your next customer",
            "Your agent offers your Calendly services and times, and gets customers booked.", ToolSignIn.Provider,
            [new("check-availability", "Offer available times", "Share open times for the services you chose.", false),
             new("create-bookings", "Book appointments", "Book the time a customer picks, or send the booking link.", true, CanAskFirst: true)]),
        new("google-calendar", "Google Calendar", "Appointments", "Keep appointments in the right place",
            "Your agent offers times you're free and adds each booking to your calendar.", ToolSignIn.Provider,
            [new("check-availability", "Offer available times", "Share times you're free, within your opening hours.", false),
             new("create-events", "Book appointments", "Add the agreed appointment to your calendar.", true, CanAskFirst: true)]),
        // A place to look things up, never a record of the work: Tasks already is that (designer, 2 November 2026).
        new("google-sheets", "Google Sheets", "Information", "Look things up in your sheets",
            "Your agent checks your sheets for answers, like delivery fees, wholesale prices or anything else you keep there.", ToolSignIn.Provider,
            [new("find-information", "Look things up", "Read the sheets you chose for answers.", false)]),
    ];
    public static ToolDefinition? Find(string id) => All.FirstOrDefault(tool => tool.Id == id);
}

/// <summary>Something found in the connected account: a Calendly event type, a calendar, a merchant account.
/// Main: the account's main calendar. Hidden: a Calendly event type kept off the public booking page.
/// Paid: takes a deposit on Calendly. ReadOnly: a calendar that can be checked but not written to.</summary>
public sealed record ToolResource(string Id, string Name, string? Detail = null, bool Main = false, bool Hidden = false,
    bool Paid = false, bool ReadOnly = false);

/// <summary>What the business chose after signing in: one part per tool, the others null. Accelerate has none.
/// Services: the Calendly event types the agent may offer.</summary>
public sealed record ToolSettings(IReadOnlyList<ToolResource>? Services = null, CalendarSettings? Calendar = null, SheetSettings? Sheet = null);

/// <summary>Google Calendar is only there to check when the business is free and add bookings (designer, 2 November
/// 2026). AddTo: the main calendar, or null when Google wasn't allowed to add events. What customers book and how long
/// it takes come from Products &amp; Services, and when, from the opening hours in Settings; the backend reads both.</summary>
public sealed record CalendarSettings(ToolResource? AddTo);

/// <summary>Google Sheets: the sheets the agent may look things up in, each picked in Google's file picker. Only the
/// tabs that are on are kept, with the columns left out. The agent reads what a tab is from its tab and column names.
/// yára only reads; it never writes to a sheet (designer, 2 November 2026).</summary>
public sealed record SheetSettings(IReadOnlyList<LookupSheet> Sheets);
public sealed record LookupSheet(string SpreadsheetId, string Name, IReadOnlyList<LookupTab> Tabs);
public sealed record LookupTab(string Name, IReadOnlyList<string> LeftOut);

/// <summary>A spreadsheet picked in Google's file picker, with each tab's header row (none: the tab can't be read).</summary>
public sealed record SheetDetails(string Id, string Name, IReadOnlyList<SheetTab> Tabs);
public sealed record SheetTab(string Name, IReadOnlyList<string> Headers);

/// <summary>Summary: one line for lists ("3 services", "Orders 2026 · Orders tab"). Granted: what the provider
/// allowed (Google lets people untick permissions). BooksInChat: Calendly's plan allows booking from yára.</summary>
public sealed record ToolConnection(Guid Id, string ToolId, string? AccountName, string? Summary, ToolSettings? Settings,
    IReadOnlyList<string> Granted, bool BooksInChat, ToolConnectionStatus Status, DateTimeOffset? ConnectedAt, int Revision);

public sealed record ToolSetup(Guid Id, Guid ConnectionId, string ToolId, string? AuthorizationUrl, string? AccountName,
    bool Authorized, IReadOnlyList<string> Granted, IReadOnlyList<ToolResource> Resources, bool BooksInChat,
    DateTimeOffset ExpiresAt, int ConnectionRevision);
public sealed record ConfigureToolRequest(ToolSettings? Settings, int ExpectedRevision);
public sealed record ToolAgentAccess(string AgentId, Guid ConnectionId, IReadOnlyList<string> Actions, ToolActionMode Mode, int Revision);
public sealed record WorkspaceToolsSnapshot(IReadOnlyList<ToolConnection> Connections, IReadOnlyList<ToolAgentAccess> AgentAccess, bool CanManage);
/// <summary>An agent to choose for a tool. Look: its face, as the business chose it.</summary>
public sealed record ToolAgentOption(string Id, string Name, string Job, string? UseCase = null, string? Look = null);

/// <summary>The one choice for a tool that acts for the business: ask first (approved on its task in Tasks) or go
/// straight ahead (designer, 2 November 2026). Reading and checking times never ask.</summary>
public sealed record AskFirstWords(string Label, string AskDetail, string Straight, string StraightDetail);

public static class ToolAccessCopy
{
    public static bool Schedules(string action) => action is "create-bookings" or "create-events";

    /// <summary>The other connected tool this agent already books through, if any: an agent books through one tool
    /// only, so there it only checks times.</summary>
    public static string? BooksThrough(string agentId, ToolConnection connection, WorkspaceToolsSnapshot snapshot) =>
        ToolCatalog.Find(connection.ToolId)?.Actions.Any(action => Schedules(action.Id)) == true
            ? snapshot.AgentAccess.Where(access => access.AgentId == agentId && access.ConnectionId != connection.Id && access.Actions.Any(Schedules))
                .Select(access => snapshot.Connections.FirstOrDefault(item => item.Id == access.ConnectionId && item.Status == ToolConnectionStatus.Connected)?.ToolId)
                .Where(tool => tool is not null).Select(tool => ToolCatalog.Find(tool!)?.Name).FirstOrDefault()
            : null;

    /// <summary>What turning a tool on gives an agent: everything the provider allowed, only checking times where it
    /// already books elsewhere.</summary>
    public static string[] ActionsToGrant(string agentId, ToolConnection connection, WorkspaceToolsSnapshot snapshot)
    {
        var allowed = (ToolCatalog.Find(connection.ToolId)?.Actions ?? []).Where(action => connection.Granted.Contains(action.Id)).Select(action => action.Id);
        return BooksThrough(agentId, connection, snapshot) is null ? [.. allowed] : [.. allowed.Where(action => !Schedules(action))];
    }

    /// <summary>What an agent does with a tool, in a few words, from the actions it has (or would have).</summary>
    public static string Ability(string toolId, IEnumerable<string> actions) => toolId switch
    {
        "accelerate" => "Sends payment links",
        "calendly" or "google-calendar" => actions.Any(Schedules) ? "Books appointments" : "Checks open times",
        "google-sheets" => "Looks things up in your sheets",
        _ => ToolCatalog.Find(toolId)?.Headline ?? "Uses this tool",
    };

    public static AskFirstWords? AskWords(string toolId) => toolId switch
    {
        "accelerate" => new("Before sending a link", "You approve each amount in Tasks first.", "Send straight away", "Sends the agreed amount without asking."),
        "calendly" or "google-calendar" => new("Before booking", "You approve each booking in Tasks first.", "Book straight away", "Books any open time without asking."),
        _ => null,
    };

    /// <summary>Whether these actions include one the business can ask to approve first.</summary>
    public static bool CanAskFirst(string toolId, IEnumerable<string> actions) =>
        (ToolCatalog.Find(toolId)?.Actions ?? []).Any(action => action.CanAskFirst && actions.Contains(action.Id));

    public static IReadOnlyList<ToolAgentAccess> Grants(Guid connection, WorkspaceToolsSnapshot snapshot) =>
        snapshot.AgentAccess.Where(access => access.ConnectionId == connection && access.Actions.Count > 0).ToArray();

    public static string AgentName(string id, IReadOnlyList<ToolAgentOption> agents) => agents.FirstOrDefault(agent => agent.Id == id)?.Name ?? "Unavailable agent";
    public static string Mode(ToolAgentAccess access, ToolDefinition tool)
    {
        var chosen = tool.Actions.Where(action => access.Actions.Contains(action.Id)).ToArray();
        if (!chosen.Any(action => action.ChangesData)) return "Checks information only";
        return chosen.Any(action => action.CanAskFirst) && access.Mode == ToolActionMode.AskFirst ? "Asks you first" : "Runs allowed actions automatically";
    }

    public static string? BookingConflict(string agent, Guid connection, WorkspaceToolsSnapshot snapshot)
    {
        var other = snapshot.AgentAccess.FirstOrDefault(access => access.AgentId == agent && access.ConnectionId != connection
            && (access.Actions.Contains("create-bookings") || access.Actions.Contains("create-events"))
            && snapshot.Connections.Any(item => item.Id == access.ConnectionId && item.Status == ToolConnectionStatus.Connected));
        var provider = snapshot.Connections.FirstOrDefault(item => item.Id == other?.ConnectionId)?.ToolId;
        return provider is null ? null : $"Already books through {ToolCatalog.Find(provider)?.Name}. Remove appointment access there before switching. Offering times is still allowed.";
    }

    public static string? Readiness(string id, string name, string? useCase, WorkspaceToolsSnapshot snapshot)
    {
        if (useCase != "bookings") return null;
        var canBook = snapshot.AgentAccess.Any(access => access.AgentId == id
            && (access.Actions.Contains("create-bookings") || access.Actions.Contains("create-events"))
            && snapshot.Connections.Any(connection => connection.Id == access.ConnectionId && connection.Status == ToolConnectionStatus.Connected));
        return canBook ? null : $"{name} can answer booking questions, but needs access to a booking tool to arrange appointments. Until then, your team handles the booking.";
    }
}

/// <summary>Workspace-scoped business tools. Credentials, provider authorization and execution belong on the server.</summary>
public interface IWorkspaceTools
{
    bool Available { get; }
    bool IsPreview { get; }
    Task<WorkspaceToolsSnapshot> ReadAsync(CancellationToken ct = default);

    /// <summary>Starts connecting, or changing a connection. A connection that's still allowed comes back already
    /// allowed, so changing its settings doesn't sign in again. SignInAgain goes to the provider: to allow more, or
    /// to use another account (chosen in the provider's own window).</summary>
    Task<ToolSetup> BeginAsync(string toolId, Guid? connectionId = null, bool signInAgain = false, CancellationToken ct = default);
    Task<ToolSetup> CheckAuthorizationAsync(Guid setupId, CancellationToken ct = default);

    /// <summary>Accelerate: the keys are sent once, checked and kept on the server, and never returned.</summary>
    Task<ToolSetup> SubmitKeysAsync(Guid setupId, string publicKey, string secretKey, CancellationToken ct = default);

    /// <summary>Google Sheets: the tabs and header rows of a spreadsheet picked in Google's file picker.</summary>
    Task<SheetDetails> ReadSheetAsync(Guid setupId, string spreadsheetId, CancellationToken ct = default);
    Task<ToolConnection> ConfigureAsync(Guid setupId, ConfigureToolRequest request, CancellationToken ct = default);
    Task DisconnectAsync(Guid connectionId, int expectedRevision, CancellationToken ct = default);
    Task<ToolAgentAccess> SetAccessAsync(ToolAgentAccess access, CancellationToken ct = default);
}

/// <summary>Do not call speculative provider endpoints or simulate a successful connection in real mode.</summary>
public sealed class UnavailableWorkspaceTools : IWorkspaceTools
{
    public bool Available => false;
    public bool IsPreview => false;
    public Task<WorkspaceToolsSnapshot> ReadAsync(CancellationToken ct = default) => Task.FromResult(new WorkspaceToolsSnapshot([], [], false));
    public Task<ToolSetup> BeginAsync(string toolId, Guid? connectionId = null, bool signInAgain = false, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolSetup> CheckAuthorizationAsync(Guid setupId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolSetup> SubmitKeysAsync(Guid setupId, string publicKey, string secretKey, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SheetDetails> ReadSheetAsync(Guid setupId, string spreadsheetId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolConnection> ConfigureAsync(Guid setupId, ConfigureToolRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DisconnectAsync(Guid connectionId, int expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolAgentAccess> SetAccessAsync(ToolAgentAccess access, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Frontend validation only; the backend must independently enforce these rules and permissions.</summary>
public static class ToolAccessRules
{
    public static string? Problem(ToolAgentAccess access, WorkspaceToolsSnapshot snapshot)
    {
        if (!snapshot.CanManage) return "You don't have permission to manage tool access in this workspace.";
        var connection = snapshot.Connections.FirstOrDefault(connection => connection.Id == access.ConnectionId);
        if (connection is null || connection.Status != ToolConnectionStatus.Connected) return "Reconnect this tool before changing agent access.";
        if (string.IsNullOrWhiteSpace(access.AgentId)) return "Choose an agent first.";
        var definition = ToolCatalog.Find(connection.ToolId);
        if (definition is null || access.Actions.Any(action => definition.Actions.All(allowed => allowed.Id != action)))
            return "This action isn't available for this tool.";
        if (access.Actions.Any(action => !connection.Granted.Contains(action)))
            return $"Allow this in {ToolSetupCopy.For(connection.ToolId).Provider} first, then give the agent access.";
        if (!Enum.IsDefined(access.Mode)) return "Choose how this agent should use the tool.";
        var schedules = access.Actions.Contains("create-bookings") || access.Actions.Contains("create-events");
        if (schedules && snapshot.AgentAccess.Any(other => other.AgentId == access.AgentId && other.ConnectionId != access.ConnectionId
            && snapshot.Connections.Any(connection => connection.Id == other.ConnectionId && connection.Status == ToolConnectionStatus.Connected)
            && (other.Actions.Contains("create-bookings") || other.Actions.Contains("create-events"))))
            return "This agent already books through another tool. Remove booking access there first to avoid duplicate appointments.";
        return null;
    }
}
