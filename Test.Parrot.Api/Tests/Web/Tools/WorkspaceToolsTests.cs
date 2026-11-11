using System.Text.Json;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Layout;
using Parrot.Web.Features.Dashboard.Catalog;
using Parrot.Web.Features.Dashboard.Tools;

namespace Test.Parrot.Api.Tests.Web.Tools;

// Controlled frontend contracts only; no dependency on demo users, fixtures or provider accounts.
public class WorkspaceToolsTests
{
    private static readonly Guid ConnectionId = Guid.Parse("d48c3eb1-742c-49b6-bbda-fc8518ca2ac7");
    private static IReadOnlyList<string> Everything(string tool) => [.. ToolCatalog.Find(tool)!.Actions.Select(action => action.Id)];
    private static ToolConnection Connection(string tool = "accelerate", ToolConnectionStatus status = ToolConnectionStatus.Connected) =>
        new(ConnectionId, tool, "Controlled account", "Controlled summary", null, Everything(tool), false, status, DateTimeOffset.UnixEpoch, 1);
    private static ToolAgentAccess Access(params string[] actions) => new("agent-under-test", ConnectionId, actions, ToolActionMode.AskFirst, 0);
    private static WorkspaceToolsSnapshot Snapshot(ToolConnection connection, params ToolAgentAccess[] grants) => new([connection], grants, true);
    private static ToolSetup Setup(string tool, IReadOnlyList<string> granted, params ToolResource[] resources) =>
        new(Guid.NewGuid(), ConnectionId, tool, null, "Controlled account", true, granted, resources, false, DateTimeOffset.UnixEpoch, 1);

    [Fact]
    public void ToolsReplacesTheUnusedCampaignsEntry()
    {
        var tools = NavMap.Primary.Single(entry => entry.Path == "/dashboard/tools");
        Assert.Equal("Tools", tools.Label);
        Assert.Equal(IconName.RiExchangeBoxFill, tools.Glyph);
        Assert.DoesNotContain(NavMap.Primary, entry => entry.Label == "Campaigns");
        Assert.Equal("Tools", NavMap.Match("/dashboard/tools")!.Label);
    }

    [Fact]
    public void OnlyTheFourApprovedToolsAreListedAndMeetIsNotASecondConnection()
    {
        Assert.Equal(new[] { "accelerate", "calendly", "google-calendar", "google-sheets" }, ToolCatalog.All.Select(tool => tool.Id));
        Assert.Null(ToolCatalog.Find("google-meet"));
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(tool => tool.Id).Distinct().Count());
    }

    [Fact]
    public void AccelerateConnectsWithKeysAndTheOthersAtTheProvider()
    {
        Assert.Equal(ToolSignIn.Keys, ToolCatalog.Find("accelerate")!.SignIn);
        Assert.All(ToolCatalog.All.Where(tool => tool.Id != "accelerate"), tool => Assert.Equal(ToolSignIn.Provider, tool.SignIn));
        Assert.Empty(ToolSetupCopy.For("accelerate").Permissions);
    }

    [Fact]
    public void PaymentAccessIsOneActionAndDoesNotSilentlyIncludeRefundsOrTransfers()
    {
        Assert.Equal(new[] { "send-payment-links" }, ToolCatalog.Find("accelerate")!.Actions.Select(action => action.Id));
        Assert.NotNull(ToolAccessRules.Problem(Access("approve-refunds"), Snapshot(Connection())));
    }

    [Fact]
    public void PaymentsAndBookingsCanWaitForTheOwnersApproval() =>
        Assert.Equal(new[] { "send-payment-links", "create-bookings", "create-events" }, ToolCatalog.All.SelectMany(tool => tool.Actions).Where(action => action.CanAskFirst).Select(action => action.Id));

    [Fact]
    public void SheetsOnlyLooksThingsUp()
    {
        var actions = ToolCatalog.Find("google-sheets")!.Actions;
        Assert.Equal(new[] { "find-information" }, actions.Select(action => action.Id));
        Assert.All(actions, action => Assert.False(action.ChangesData));
    }

    [Fact]
    public void AConnectedToolDoesNotImplyAnyAgentAccess()
    {
        var snapshot = Snapshot(Connection());
        Assert.Empty(snapshot.AgentAccess);
        Assert.Null(ToolAccessRules.Problem(Access(), snapshot));
    }

    [Fact]
    public void SelectedActionsAreAllowedButUnknownActionsAreNot()
    {
        Assert.Null(ToolAccessRules.Problem(Access("send-payment-links"), Snapshot(Connection())));
        Assert.NotNull(ToolAccessRules.Problem(Access("find-information"), Snapshot(Connection())));
    }

    [Fact]
    public void ActionsTheProviderDidNotAllowCannotBeGiven()
    {
        var calendar = Connection("google-calendar") with { Granted = ["check-availability"] };
        Assert.NotNull(ToolAccessRules.Problem(Access("create-events"), Snapshot(calendar)));
        Assert.Null(ToolAccessRules.Problem(Access("check-availability"), Snapshot(calendar)));
    }

    [Theory]
    [InlineData(ToolConnectionStatus.NeedsAttention)]
    [InlineData(ToolConnectionStatus.SetupIncomplete)]
    public void NonConnectedToolsCannotReceiveNewAccess(ToolConnectionStatus status) =>
        Assert.NotNull(ToolAccessRules.Problem(Access("send-payment-links"), Snapshot(Connection(status: status))));

    [Fact]
    public void MissingConnectionsAndMissingAgentsCannotReceiveAccess()
    {
        Assert.NotNull(ToolAccessRules.Problem(Access("send-payment-links"), new WorkspaceToolsSnapshot([], [], true)));
        Assert.NotNull(ToolAccessRules.Problem(Access("send-payment-links") with { AgentId = "" }, Snapshot(Connection())));
    }

    [Fact]
    public void InvalidConfirmationModesAreRejected() =>
        Assert.NotNull(ToolAccessRules.Problem(Access("send-payment-links") with { Mode = (ToolActionMode)99 }, Snapshot(Connection())));

    [Fact]
    public void ReadOnlyWorkspaceMembersCannotChangeAgentAccess() =>
        Assert.NotNull(ToolAccessRules.Problem(Access("send-payment-links"), Snapshot(Connection()) with { CanManage = false }));

    [Fact]
    public void OneAgentCannotCreateBookingsThroughTwoDifferentTools()
    {
        var calendar = Connection("google-calendar");
        var calendly = Connection("calendly") with { Id = Guid.NewGuid() };
        var snapshot = new WorkspaceToolsSnapshot([calendar, calendly], [new("agent-under-test", calendly.Id, ["create-bookings"], ToolActionMode.Automatic, 1)], true);
        Assert.NotNull(ToolAccessRules.Problem(Access("create-events"), snapshot));
        Assert.Null(ToolAccessRules.Problem(Access("check-availability"), snapshot));
        Assert.Null(ToolAccessRules.Problem(Access("create-events") with { AgentId = "a-different-agent" }, snapshot));
    }

    [Fact]
    public void UpdatingTheExistingBookingGrantDoesNotConflictWithItself()
    {
        var grant = Access("create-bookings");
        Assert.Null(ToolAccessRules.Problem(grant, Snapshot(Connection("calendly"), grant)));
    }

    [Fact]
    public async Task RealModeNeverPretendsToConnectWithoutAnApi()
    {
        IWorkspaceTools service = new UnavailableWorkspaceTools();
        Assert.False(service.Available);
        Assert.False(service.IsPreview);
        Assert.False((await service.ReadAsync()).CanManage);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.BeginAsync("accelerate"));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SubmitKeysAsync(Guid.NewGuid(), "public", "secret"));
    }

    [Fact]
    public void SnapshotsRoundTripWithoutCredentialsOrDemoFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Snapshot(Connection(), Access("send-payment-links")), options);
        var snapshot = JsonSerializer.Deserialize<WorkspaceToolsSnapshot>(json, options)!;
        Assert.Equal(ConnectionId, snapshot.Connections[0].Id);
        Assert.Equal("send-payment-links", snapshot.AgentAccess[0].Actions[0]);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedAccessSummariesDoNotGrantAnything()
    {
        var grant = Access("send-payment-links");
        IReadOnlyList<ToolAgentOption> agents = [new("agent-under-test", "Bola", "Sales & orders")];
        Assert.Equal("Bola", ToolAccessCopy.AgentName(grant.AgentId, agents));
        Assert.Equal("Unavailable agent", ToolAccessCopy.AgentName("removed-agent", agents));
        Assert.Equal("Asks you first", ToolAccessCopy.Mode(grant, ToolCatalog.Find("accelerate")!));
        Assert.Equal("Runs allowed actions automatically", ToolAccessCopy.Mode(Access("create-bookings") with { Mode = ToolActionMode.Automatic }, ToolCatalog.Find("calendly")!));
        Assert.Equal("Checks information only", ToolAccessCopy.Mode(Access("check-availability"), ToolCatalog.Find("calendly")!));
        Assert.Single(ToolAccessCopy.Grants(ConnectionId, Snapshot(Connection(), grant)));
        Assert.Empty(ToolAccessCopy.Grants(ConnectionId, Snapshot(Connection(), Access())));
    }

    [Fact]
    public void BookingReadinessDependsOnThisAgentsActiveAppointmentAccess()
    {
        var calendar = Connection("google-calendar");
        Assert.NotNull(ToolAccessCopy.Readiness("agent-under-test", "Bola", "bookings", Snapshot(calendar)));
        Assert.NotNull(ToolAccessCopy.Readiness("agent-under-test", "Bola", "bookings", Snapshot(calendar, Access("check-availability"))));
        Assert.Null(ToolAccessCopy.Readiness("agent-under-test", "Bola", "bookings", Snapshot(calendar, Access("create-events"))));
        Assert.NotNull(ToolAccessCopy.Readiness("agent-under-test", "Bola", "bookings", Snapshot(calendar with { Status = ToolConnectionStatus.NeedsAttention }, Access("create-events"))));
        Assert.Null(ToolAccessCopy.Readiness("agent-under-test", "Bola", "support", Snapshot(calendar)));
    }

    [Fact]
    public void BookingConflictCanBeExplainedBeforeTheOwnerSelectsAnAction()
    {
        var calendar = Connection("google-calendar");
        var calendly = Connection("calendly") with { Id = Guid.NewGuid() };
        var snapshot = new WorkspaceToolsSnapshot([calendar, calendly], [new("agent-under-test", calendly.Id, ["create-bookings"], ToolActionMode.AskFirst, 1)], true);
        Assert.Contains("Calendly", ToolAccessCopy.BookingConflict("agent-under-test", calendar.Id, snapshot));
        Assert.Null(ToolAccessCopy.BookingConflict("another-agent", calendar.Id, snapshot));
        Assert.Null(ToolAccessCopy.BookingConflict("agent-under-test", calendly.Id, snapshot));
    }

    [Fact]
    public async Task RealModeDoesNotCreateOrExecuteTestTasks()
    {
        IToolActionRequests requests = new UnavailableToolActionRequests();
        Assert.False(requests.CanTry);
        Assert.Null(await requests.ForTaskAsync("controlled-task"));
        await Assert.ThrowsAsync<NotSupportedException>(() => requests.TryAsync(new(Guid.NewGuid(), ConnectionId, "agent", "create-events")));
        await Assert.ThrowsAsync<NotSupportedException>(() => requests.DecideAsync(Guid.NewGuid(), true, 1));
        await Assert.ThrowsAsync<NotSupportedException>(() => requests.RetryAsync(Guid.NewGuid(), 1));
    }

    [Fact]
    public void GoogleShowsATickBoxPerPermissionOnlyWhenItAsksForMoreThanOne()
    {
        Assert.True(ToolSetupCopy.For("google-calendar").Tickable);
        Assert.Equal(new[] { "check-availability", "create-events" }, ToolSetupCopy.For("google-calendar").Permissions.Select(permission => permission.Key));
        Assert.False(ToolSetupCopy.For("google-sheets").Tickable);
        Assert.False(ToolSetupCopy.For("calendly").Tickable);
        Assert.Contains("only the specific Google Drive files", ToolSetupCopy.For("google-sheets").Permissions.Single().Text);
    }

    [Fact]
    public void CalendlyStartsWithEveryServiceItFound()
    {
        var setup = Setup("calendly", Everything("calendly"), new ToolResource("consultation", "Consultation", "30 min"), new ToolResource("vip", "VIP preview", "60 min", Hidden: true));
        var draft = CalendlyDraft.From(setup, null);
        Assert.Equal(new[] { "consultation", "vip" }, draft.Services.Order());
        Assert.Equal(2, draft.ToSettings().Services!.Count);
        draft.Services.Clear();
        Assert.NotNull(draft.Problem);
    }

    [Fact]
    public void CalendarBooksIntoTheMainCalendarWithNothingToChoose()
    {
        var setup = Setup("google-calendar", Everything("google-calendar"), new ToolResource("main", "Main", Main: true));
        var draft = CalendarDraft.From(setup);
        Assert.True(draft.CanBook);
        Assert.Null(draft.Problem);
        Assert.Equal("Main", draft.ToSettings().Calendar!.AddTo!.Name);
    }

    [Fact]
    public void WithoutPermissionToAddEventsTheCalendarOnlyOffersTimes()
    {
        var setup = Setup("google-calendar", ["check-availability"], new ToolResource("main", "Main", Main: true));
        var draft = CalendarDraft.From(setup);
        Assert.False(draft.CanBook);
        Assert.Null(draft.Problem);
        Assert.Null(draft.ToSettings().Calendar!.AddTo);
    }

    [Fact]
    public void OpeningHoursReadPlainly()
    {
        Assert.Equal("Mon–Sat", BookingFormat.Days([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday]));
        Assert.Equal("Mon, Wed, Fri", BookingFormat.Days([DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday]));
        Assert.Equal("6:30pm", BookingFormat.Time(new(18, 30)));
        Assert.Equal("Mon–Fri, 9am–6pm · Sat, 9am–4pm", BookingFormat.OpeningHours(global::Parrot.Web.Features.Settings.BusinessDetails.Defaults.Hours));
        Assert.Null(BookingFormat.OpeningHours([]));
    }

    private static SheetDetails Delivery => new("delivery", "Delivery fees", [
        new("Lagos", ["Area", "Fee (₦)", "Delivery time", "Internal note"]),
        new("Outside Lagos", ["State", "Fee (₦)"]), new("Notes", [])]);

    [Fact]
    public void APickedSheetIsOnInFullExceptColumnsThatLookPrivate()
    {
        var draft = SheetDraft.From(null, []);
        Assert.NotNull(draft.Problem);
        draft.Add(Delivery);
        draft.Add(Delivery);
        Assert.Single(draft.Sheets);
        Assert.Null(draft.Problem);
        Assert.Equal(new[] { true, true, false }, draft.Sheets[0].Tabs.Select(tab => tab.On));
        Assert.Equal(new[] { "Internal note" }, draft.Sheets[0].Tabs[0].LeftOut);
        var saved = draft.ToSettings().Sheet!.Sheets.Single();
        Assert.Equal(new[] { "Lagos", "Outside Lagos" }, saved.Tabs.Select(tab => tab.Name));
        Assert.Equal(new[] { "Internal note" }, saved.Tabs[0].LeftOut);
    }

    [Theory]
    [InlineData("Internal note", true)]
    [InlineData("Cost price (₦)", true)]
    [InlineData("Supplier phone", true)]
    [InlineData("Delivery cost", false)]
    [InlineData("Wholesale price (₦)", false)]
    [InlineData("Notebook size", false)]
    public void ColumnsThatLookPrivateStartLeftOut(string header, bool hidden) => Assert.Equal(hidden, SheetDraft.LooksPrivate(header));

    [Fact]
    public void ASavedSheetComesBackAsItIsInGoogleNowAndNewTabsStartOff()
    {
        var saved = new SheetSettings([new("delivery", "Delivery fees", [new("Lagos", ["Internal note"]), new("Old tab", [])])]);
        var now = Delivery with { Tabs = [.. Delivery.Tabs, new("Abuja", ["Area", "Fee (₦)"])] };
        var draft = SheetDraft.From(saved, [now]);
        var tabs = draft.Sheets.Single().Tabs;
        Assert.Equal(new[] { "Lagos", "Outside Lagos", "Notes", "Abuja" }, tabs.Select(tab => tab.Tab.Name));
        Assert.Equal(new[] { true, false, false, false }, tabs.Select(tab => tab.On));
        Assert.Equal(new[] { "Internal note" }, tabs[0].LeftOut);
        Assert.Empty(SheetDraft.From(saved, []).Sheets);
    }

    [Fact]
    public void ATabWithNoColumnNamesCantBeTurnedOnOrSaved()
    {
        var draft = SheetDraft.From(null, []);
        draft.Add(new("notes", "Notes", [new("Sheet1", [])]));
        Assert.False(draft.Sheets[0].Tabs[0].Readable);
        Assert.Contains("no tab your agent can read", draft.Problem);
    }

    [Fact]
    public void EachSheetNeedsATabOnAndEachTabAColumnShown()
    {
        var draft = SheetDraft.From(null, []);
        draft.Add(Delivery);
        foreach (var tab in draft.Sheets[0].Tabs) tab.On = false;
        Assert.Contains("Turn on a tab", draft.Problem);
        draft.Sheets[0].Tabs[1].On = true;
        draft.Sheets[0].Tabs[1].LeftOut.UnionWith(["State", "Fee (₦)"]);
        Assert.Contains("Show at least one column", draft.Problem);
    }
}
