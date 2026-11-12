using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.TaskRequests;
using Parrot.Web.Components.Pages.Dashboard.Team;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Channels.Services;
using Parrot.Web.Features.Dashboard.Channels.State;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Parrot.Web.Features.Dashboard.Team;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Production components and public contracts with controlled HTTP/store fakes, never the local demo dataset.
public sealed class TeamTaskContentModalTests
{
    [Fact]
    public async Task InvitationUsesContentModalNativeFormAndKeepsLimitsProgressive()
    {
        await using var page = await Page.Team();
        var html = await page.Html();
        Assert.Equal("Invite team member", page.Modal.Title);
        Assert.DoesNotContain("content-modal--compact-heading", html);
        Assert.Equal("We'll email them a link to join. It works for 7 days.", page.Modal.Subtitle);
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Equal(1, decoded.Split(page.Modal.Subtitle, StringSplitOptions.None).Length - 1);
        Assert.True(decoded.IndexOf(page.Modal.Subtitle!, StringComparison.Ordinal) < decoded.IndexOf("<form", StringComparison.Ordinal));
        Assert.True(page.Modal.OnSubmit.HasDelegate);
        Assert.False(page.Modal.CloseOnBackdrop);
        Assert.Equal("invite-full-name", page.Modal.InitialFocusId);
        Assert.Contains("Limit access", html); Assert.DoesNotContain("invite__limits", html);
        // With more than one workspace, where they work is a field of its own (designer, 9 November 2026), not a limit.
        Assert.Contains("id=\"invite-workspaces\"", html);
        Assert.DoesNotContain("dialog-backdrop", html);
        AssertAssociatedForm(html);
    }

    [Fact]
    public async Task InvitationErrorsFocusNameEmailRoleThenLimitsWithoutSending()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        // A role starts chosen (designer, 8 November 2026), so what it does shows at once.
        Assert.False(string.IsNullOrEmpty(page.Value<string>(root, "_roleId")));
        await page.Call(root, "SaveAsync"); Assert.Equal("invite-full-name", page.Modal.InitialFocusId);
        await page.Set(root, "_fullName", "Controlled person");
        await page.Call(root, "SaveAsync"); Assert.Equal("invite-email", page.Modal.InitialFocusId);
        await page.Set(root, "_email", "controlled@example.test"); await page.Set(root, "_roleId", (string?)null);
        await page.Call(root, "SaveAsync"); Assert.Equal("invite-access-role", page.Modal.InitialFocusId);
        await page.Set(root, "_roleId", "controlled-role"); await page.Call(root, "ToggleWorkspace", "workspace-b");
        await page.Call(root, "SaveAsync"); Assert.Equal("invite-workspaces", page.Modal.InitialFocusId);
        await page.Call(root, "ToggleWorkspace", "workspace-b"); await page.Set(root, "_allChannels", false);
        await page.Call(root, "SaveAsync"); Assert.Equal("invite-access-limits", page.Modal.InitialFocusId);
        Assert.True(page.Value<bool>(root, "_limiting")); Assert.Empty(page.Http.Requests);
    }

    [Fact]
    public async Task AnEmailAlreadyInvitedIsCaughtBeforeSending()
    {
        var waiting = new TeamInvite(Guid.NewGuid(), "Waiting person", "waiting@example.test", [], DateTime.UtcNow, DateTime.UtcNow.AddDays(5));
        await using var page = await Page.Team(invites: [waiting]); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Waiting person"); await page.Set(root, "_email", "WAITING@example.test");
        Assert.Equal("Waiting person is already invited.", root.GetType().GetProperty("EmailProblem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root));
        await page.Call(root, "SaveAsync");
        Assert.Equal("invite-email", page.Modal.InitialFocusId); Assert.Empty(page.Http.Requests);
    }

    [Fact]
    public async Task ASentInviteWithALinkStaysOpenToCopyItThenCloses()
    {
        await using var page = await Page.Team(link: "invite/controlled"); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Controlled person"); await page.Set(root, "_email", "controlled@example.test");
        await page.Call(root, "SaveAsync");
        Assert.Equal("Invite sent", page.Modal.Title); Assert.Equal(0, page.Saved);
        await page.Call(root, "DoneAsync"); Assert.Equal(1, page.Saved);
    }

    // Designer, 9 November 2026: an invite starts on the workspace the owner is in; more is their choice.
    [Fact]
    public async Task InvitationStartsOnTheCurrentWorkspaceWithEveryChannel()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        Assert.Contains("Controlled workspace B", await page.Html());
        await page.Set(root, "_fullName", " Controlled person "); await page.Set(root, "_email", " controlled@example.test ");
        await page.Set(root, "_roleId", "controlled-role"); await page.Call(root, "SaveAsync");
        var request = Assert.Single(page.Http.Requests);
        Assert.Equal("POST", request.Method); Assert.Equal("/api/team/invites", request.Path);
        Assert.Equal("Controlled person", request.Body.GetProperty("fullName").GetString());
        Assert.Equal("controlled@example.test", request.Body.GetProperty("email").GetString());
        var grant = Assert.Single(request.Body.GetProperty("grants").EnumerateArray());
        Assert.Equal("workspace-b", grant.GetProperty("workspaceId").GetString());
        Assert.Equal("controlled-role", grant.GetProperty("roleId").GetString());
        Assert.Empty(grant.GetProperty("channels").EnumerateArray()); Assert.Equal(1, page.Saved);
    }

    [Fact]
    public async Task SeveralWorkspacesAreAGrantEachAndAllWorkspacesIsOne()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Controlled person"); await page.Set(root, "_email", "controlled@example.test");
        await page.Call(root, "ToggleWorkspace", "workspace-a");
        Assert.Contains("Controlled workspace A and Controlled workspace B", await page.Html());
        await page.Call(root, "SaveAsync");
        var grants = Assert.Single(page.Http.Requests).Body.GetProperty("grants").EnumerateArray().Select(g => g.GetProperty("workspaceId").GetString()).ToList();
        Assert.Equal(new string?[] { "workspace-a", "workspace-b" }, grants);

        await using var all = await Page.Team(); var form = all.Root<InviteModal>();
        await all.Set(form, "_fullName", "Controlled person"); await all.Set(form, "_email", "controlled@example.test");
        await all.Call(form, "ChooseAllWorkspaces"); Assert.Contains("Including workspaces you add later.", await all.Html());
        await all.Call(form, "SaveAsync");
        var grant = Assert.Single(Assert.Single(all.Http.Requests).Body.GetProperty("grants").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, grant.GetProperty("workspaceId").ValueKind);
    }

    [Fact]
    public async Task InvitationLimitsProduceTheSameExplicitGrants()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Controlled person"); await page.Set(root, "_email", "controlled@example.test");
        await page.Set(root, "_roleId", "controlled-role"); await page.Set(root, "_allChannels", false);
        await page.Keys(root, "_channels", "instagram");
        await page.Call(root, "SaveAsync");
        var grant = Assert.Single(Assert.Single(page.Http.Requests).Body.GetProperty("grants").EnumerateArray());
        Assert.Equal("workspace-b", grant.GetProperty("workspaceId").GetString());
        Assert.Equal("instagram", Assert.Single(grant.GetProperty("channels").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task EditAccessStartsFromExistingGrantsAndUsesTheSameUpdateEndpoint()
    {
        var member = new TeamMember("controlled-member", "Controlled person", "controlled@example.test", false,
            [new("workspace-b", "controlled-role", "Controlled role", false, ["instagram"])], DateTime.UtcNow);
        await using var page = await Page.Team(member); var root = page.Root<InviteModal>();
        Assert.Equal("controlled@example.test. Changes apply the next time they use yára.", page.Modal.Subtitle);
        Assert.True(page.Value<bool>(root, "_limiting")); Assert.Equal("invite-access-role", page.Modal.InitialFocusId);
        await page.Call(root, "RequestCloseAsync"); Assert.Equal(1, page.Closed); // Existing values aren't dirty.
        await page.Call(root, "SaveAsync"); var request = Assert.Single(page.Http.Requests);
        Assert.Equal("PUT", request.Method); Assert.Equal("/api/team/members/controlled-member", request.Path);
        Assert.DoesNotContain("fullName", request.Body.GetRawText()); Assert.Equal(1, page.Saved);
    }

    [Fact]
    public async Task InvitationFailurePreservesFieldsAndCanRetry()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Controlled person"); await page.Set(root, "_email", "controlled@example.test"); await page.Set(root, "_roleId", "controlled-role");
        page.Http.Fail = true; await page.Call(root, "SaveAsync");
        Assert.Contains("couldn&#x27;t send", await page.Html()); Assert.False(page.Modal.Busy);
        Assert.Equal("controlled@example.test", page.Value<string>(root, "_email")); Assert.Equal(0, page.Saved);
        page.Http.Fail = false; await page.Call(root, "SaveAsync"); Assert.Equal(1, page.Saved);
    }

    [Fact]
    public async Task InvitationCloseKeepsEditsUntilDiscardAndClosesItsRoleMenu()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        await page.Set(root, "_fullName", "Controlled person"); await page.Set(root, "_roleOpen", true);
        await page.Call(root, "RequestCloseAsync"); Assert.True(page.Value<bool>(root, "_discard")); Assert.False(page.Value<bool>(root, "_roleOpen"));
        Assert.Equal(0, page.Closed); await page.Call(root, "KeepEditing"); Assert.Equal("Controlled person", page.Value<string>(root, "_fullName"));
        await page.Call(root, "RequestCloseAsync"); await page.Call(root, "DiscardAsync"); Assert.Equal(1, page.Closed); Assert.Empty(page.Http.Requests);
    }

    [Fact]
    public async Task InvitationDoesNotTreatExpandingLimitsAsAnEditAndBlocksBusyExit()
    {
        await using var page = await Page.Team(); var root = page.Root<InviteModal>();
        await page.Set(root, "_limiting", true); await page.Call(root, "RequestCloseAsync"); Assert.Equal(1, page.Closed);
        await page.Set(root, "_saving", true); await page.Call(root, "RequestCloseAsync"); await page.Call(root, "SaveAsync");
        Assert.Equal(1, page.Closed); Assert.True(page.Modal.Busy); Assert.Empty(page.Http.Requests);
        Assert.Contains("<fieldset", await page.Html()); Assert.Contains("disabled", await page.Html());
    }

    [Fact]
    public async Task TaskUsesContentModalNativeFormAndKeepsOptionalSectionsClosed()
    {
        await using var page = await Page.Task(); var html = await page.Html();
        Assert.Equal("New task", page.Modal.Title); Assert.Equal(DialogSize.Wide, page.Modal.Size);
        Assert.DoesNotContain("content-modal--compact-heading", html);
        Assert.Equal("new-task-what", page.Modal.InitialFocusId); Assert.True(page.Modal.OnSubmit.HasDelegate); AssertAssociatedForm(html);
        Assert.DoesNotContain("Their name", html); Assert.DoesNotContain("Price (optional)", html); Assert.DoesNotContain("dialog-backdrop", html);
        var root = page.Root<NewTaskDialog>(); await page.Call(root, "SaveAsync");
        Assert.Equal("new-task-what", page.Modal.InitialFocusId); Assert.Equal(0, page.Store.Creates);
    }

    [Fact]
    public async Task TaskCreationPreservesCustomerItemsChargesTimingAndAssignment()
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        await page.Call(root, "OnText", "Prepare a quote\nFor a controlled booking."); await page.Call(root, "PickKind", TaskKind.Quote);
        await page.Call(root, "PickCustomer", page.Store.Customer); await page.Set(root, "_address", "Controlled address");
        await page.Call(root, "ShowItems"); await page.Call(root, "AddFromCatalog", new TaskItem("Controlled item", 1200));
        await page.Item(root, 0, "Quantity", "2"); await page.Charge(root, "Controlled fee", "300");
        await page.Set(root, "_dueDay", new DateOnly(2026, 11, 10)); await page.Set(root, "_dueTime", new TimeOnly(9, 30));
        await page.Set(root, "_assignee", page.Store.Person); await page.Set(root, "_urgent", true); await page.Call(root, "SaveAsync");
        var draft = Assert.Single(page.Store.Drafts);
        Assert.Equal(TaskKind.Quote, draft.Kind); Assert.Equal("Prepare a quote", draft.Title); Assert.Equal("For a controlled booking.", draft.Summary);
        Assert.Equal("controlled-thread", draft.Customer?.ConversationId); Assert.Equal("Controlled address", draft.Address);
        Assert.Equal(2700m, draft.Total); Assert.Equal(2, draft.Lines![0].Quantity); Assert.True(draft.Lines[1].Charge);
        Assert.True(draft.Urgent); Assert.True(draft.DueHasTime); Assert.NotNull(draft.DueAt); Assert.Equal(page.Store.Person.Id, draft.Assignee?.Id);
        Assert.Equal(1, page.Saved);
    }

    [Fact]
    public async Task TaskValidationFocusesTheActualInvalidOptionalField()
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        await page.Call(root, "OnText", "Controlled task"); await page.Call(root, "PickWalkIn");
        await page.Call(root, "SaveAsync"); Assert.Equal("new-task-walk-name", page.Modal.InitialFocusId);
        await page.Set(root, "_walkName", "Controlled customer"); await page.Set(root, "_walkPhone", "bad phone");
        await page.Call(root, "SaveAsync"); Assert.Equal("new-task-walk-phone", page.Modal.InitialFocusId);
        await page.Set(root, "_walkPhone", ""); await page.Call(root, "ShowItems"); await page.Item(root, 0, "Price", "bad amount");
        await page.Call(root, "SaveAsync"); Assert.EndsWith("-price", page.Modal.InitialFocusId); Assert.Contains(page.Modal.InitialFocusId!, await page.Html());
        Assert.Equal(0, page.Store.Creates);
    }

    [Fact]
    public async Task TaskCanRetryWithoutLosingEnteredDetails()
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        await page.Call(root, "OnText", "Controlled task"); page.Store.Fail = true; await page.Call(root, "SaveAsync");
        Assert.Equal("Controlled task", page.Value<string>(root, "_text")); Assert.False(page.Modal.Busy); Assert.Equal(0, page.Saved);
        page.Store.Fail = false; await page.Call(root, "SaveAsync"); Assert.Equal(1, page.Saved); Assert.Equal(2, page.Store.Creates);
    }

    [Fact]
    public async Task TaskExitChecksEditsAndDoesNotTreatAnEmptyExpandedSectionAsData()
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        await page.Call(root, "ShowItems"); await page.Call(root, "CloseAsync"); Assert.Equal(1, page.Closed); Assert.False(page.Value<bool>(root, "_discard"));
        await page.Call(root, "OnText", "Controlled task"); await page.Set(root, "_openMenu", "kind"); await page.Call(root, "CloseAsync");
        Assert.True(page.Value<bool>(root, "_discard")); Assert.Null(page.Value<string?>(root, "_openMenu")); Assert.Equal(1, page.Closed);
        await page.Call(root, "KeepEditing"); Assert.Equal("Controlled task", page.Value<string>(root, "_text"));
        await page.Call(root, "CloseAsync"); await page.Call(root, "DiscardAsync"); Assert.Equal(2, page.Closed); Assert.Equal(0, page.Store.Creates);
    }

    [Fact]
    public async Task TaskSavingBlocksRepeatedSubmissionAndClosing()
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        await page.Call(root, "OnText", "Controlled task"); await page.Set(root, "_saving", true);
        await page.Call(root, "SaveAsync"); await page.Call(root, "CloseAsync");
        Assert.Equal(0, page.Store.Creates); Assert.Equal(0, page.Closed); Assert.True(page.Modal.Busy);
    }

    [Fact]
    public async Task EditTaskRetainsTheExistingDraftAndUsesEditRatherThanCreate()
    {
        var task = TaskStore.Record(); task.Title = "Existing controlled task"; task.Summary = "Existing details";
        task.Customer = new("Controlled customer", "instagram", null, "controlled-thread"); task.Address = "Existing address";
        task.Lines = [new("Existing item", 3, 250, "Existing note"), new("Existing fee", 1, 50, Charge: true)]; task.Total = 800;
        await using var page = await Page.Task(task); var root = page.Root<NewTaskDialog>();
        Assert.Equal("Edit task", page.Modal.Title); Assert.True(page.Value<bool>(root, "_showItems"));
        await page.Call(root, "CloseAsync"); Assert.Equal(1, page.Closed); Assert.False(page.Value<bool>(root, "_discard"));
        await page.Call(root, "SaveAsync"); Assert.Equal(0, page.Store.Creates); Assert.Equal(task.Id, page.Store.EditedId);
        var draft = Assert.Single(page.Store.Drafts); Assert.Equal("Existing controlled task", draft.Title); Assert.Equal(800m, draft.Total);
        Assert.Equal("Existing note", draft.Lines![0].Note); Assert.True(draft.Lines[1].Charge); Assert.Equal(1, page.Saved);
    }

    private static void AssertAssociatedForm(string html)
    {
        var id = System.Text.RegularExpressions.Regex.Match(html, "<form id=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(id); Assert.Contains($"form=\"{id}\"", html); Assert.Contains("type=\"submit\"", html);
    }

    [Theory]
    [InlineData("USD", "$")]
    [InlineData("GBP", "£")]
    [InlineData("GHS", "GH₵")]
    public async Task NewTaskCapturesTheWorkspaceCurrencyAndKeepsItDuringEditing(string currency, string symbol)
    {
        await using var page = await Page.Task(); var root = page.Root<NewTaskDialog>();
        root.Currency = new(currency); await page.Set(root, "_wasOpen", false); await page.Call(root, "OnParametersSetAsync");
        await page.Call(root, "OnText", "Controlled task"); await page.Call(root, "ShowItems");
        await page.Item(root, 0, "Name", "Controlled item"); await page.Item(root, 0, "Price", "25.50");
        root.Currency = new("NGN"); await page.Call(root, "OnParametersSetAsync");
        Assert.Contains(symbol, System.Net.WebUtility.HtmlDecode(await page.Html()));
        await page.Call(root, "SaveAsync");
        var draft = Assert.Single(page.Store.Drafts); Assert.Equal(currency, draft.CurrencyCode); Assert.Equal(25.50m, draft.Total);
    }

    [Fact]
    public async Task ExistingTaskKeepsItsOriginalCurrencyWhenWorkspaceDefaultDiffers()
    {
        var task = TaskStore.Record(); task.CurrencyCode = "USD";
        await using var page = await Page.Task(task); var root = page.Root<NewTaskDialog>();
        root.Currency = new("GBP"); await page.Set(root, "_wasOpen", false); await page.Call(root, "OnParametersSetAsync");
        await page.Call(root, "SaveAsync"); Assert.Equal("USD", Assert.Single(page.Store.Drafts).CurrencyCode);
    }

    private sealed record Request(string Method, string Path, JsonElement Body);
    private sealed class TeamHttp : HttpMessageHandler
    {
        public string? Link;
        public bool Fail; public List<Request> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            if (Fail) throw new HttpRequestException("Controlled failure");
            return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Post
                ? JsonContent.Create(new TeamInvite(Guid.Empty, "Controlled person", "controlled@example.test", [], DateTime.UtcNow, DateTime.UtcNow.AddDays(7), Link))
                : JsonContent.Create(new TeamMember("controlled-member", "Controlled person", "controlled@example.test", false, [], DateTime.UtcNow)) };
        }
    }
    private sealed class TaskStore : ITaskStore
    {
        public bool Available => true; public TaskPerson Me => new("controlled-me", "Controlled owner");
        public TaskPerson Person => new("controlled-person", "Controlled teammate");
        public TaskCustomer Customer => new("Controlled customer", "instagram", null, "controlled-thread");
        public event Action? Changed { add { } remove { } }
        public int Creates; public bool Fail; public string? EditedId; public List<TaskDraft> Drafts = [];
        public static TaskRequest Record() => new() { Id = "controlled-task", Kind = TaskKind.Other, Title = "Controlled task", RaisedBy = "Controlled owner", RaisedAt = DateTime.UtcNow };
        public Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskRequest>>([]);
        public Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskPerson>>([Me, Person]);
        public Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskCustomer>>([Customer]);
        public Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskItem>>([new("Controlled item", 1200)]);
        public Task<TaskRequest> CreateAsync(TaskDraft draft, CancellationToken ct = default) { Creates++; if (Fail) throw new HttpRequestException("Controlled failure"); Drafts.Add(draft); return Task.FromResult(Record()); }
        public Task EditAsync(string id, TaskDraft draft, CancellationToken ct = default) { if (Fail) throw new HttpRequestException("Controlled failure"); EditedId = id; Drafts.Add(draft); return Task.CompletedTask; }
        public Task SetStateAsync(string id, TaskState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUrgentAsync(string id, bool value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetDueAsync(string id, DateTime? due, bool time, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignAsync(string id, TaskPerson? person, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PostUpdateAsync(string id, string text, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Page : IAsyncDisposable
    {
        public TeamHttp Http = new(); public TaskStore Store = new(); public int Saved, Closed;
        private Capture _capture = new(); private ServiceProvider _services = null!; private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _html;
        public T Root<T>() => _capture.Items.OfType<T>().First();
        public ContentModal Modal => _capture.Items.OfType<ContentModal>().Last(m => m.Open);
        public static async Task<Page> Team(TeamMember? member = null, TeamInvite[]? invites = null, string? link = null)
        {
            var page = new Page(); page.Http.Link = link;
            await page.Open<InviteModal>(new() { ["Catalog"] = new TeamCatalog([new("controlled-role", "Controlled role", false, ["inbox"], 0)], [new("inbox", "Inbox")], [new("instagram", "Instagram")]),
                ["Workspaces"] = new TeamWorkspace[] { new("workspace-a", "Controlled workspace A", null), new("workspace-b", "Controlled workspace B", null) }, ["Member"] = member, ["Invites"] = (IReadOnlyList<TeamInvite>)(invites ?? []),
                ["OnSaved"] = EventCallback.Factory.Create<InviteSaved>(page, _ => page.Saved++), ["OnCancel"] = EventCallback.Factory.Create(page, () => page.Closed++) });
            return page;
        }
        public static async Task<Page> Task(TaskRequest? task = null)
        {
            var page = new Page(); await page.Open<NewTaskDialog>(new() { ["Open"] = true, ["Editing"] = task,
                ["OnClose"] = EventCallback.Factory.Create(page, () => page.Closed++), ["OnCreated"] = EventCallback.Factory.Create<TaskRequest>(page, _ => page.Saved++) }); return page;
        }
        private async Task Open<T>(Dictionary<string, object?> parameters) where T : IComponent
        {
            var js = new Js(); var auth = new AuthState(); auth.SetUser(new AuthResponse { UserId = "controlled-me", Token = "controlled", Email = "controlled@example.test", WorkspaceId = "workspace-b" });
            var channels = new ChannelsState(); channels.ConnectChannel("instagram", "Controlled account", "2026-11-06");
            _services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new TestNavigation()).AddSingleton(auth).AddSingleton<IJSRuntime>(js).AddSingleton<AuroraInterop>().AddSingleton<ToastService>().AddSingleton<UserClock>()
                .AddSingleton(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider())).AddSingleton<TokenStorageService>()
                .AddSingleton(channels).AddSingleton<IComponentActivator>(_capture)
                .AddSingleton(new HttpClient(Http) { BaseAddress = new Uri("http://localhost/") }).AddSingleton<TeamClient>()
                .AddSingleton<IIntegrationsService, IntegrationsService>().AddSingleton<ChannelStatusLoader>().AddSingleton<ITaskStore>(Store).BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
            _html = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_html.ToHtmlString);
        public T Value<T>(object c, string name) => (T)c.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
        private static void Render(object c) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null);
        public Task Set(object c, string name, object? value) => _renderer.Dispatcher.InvokeAsync(() => { c.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c, value); Render(c); });
        public Task Call(object c, string name, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () => { var result = c.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, args); if (result is Task task) await task; Render(c); });
        public Task Keys(object c, string name, string value) => _renderer.Dispatcher.InvokeAsync(() => { Value<HashSet<string>>(c, name).Add(value); Render(c); });
        public Task Item(object c, int index, string name, object value) => _renderer.Dispatcher.InvokeAsync(() => { var row = Value<IList>(c, "_items")[index]!; row.GetType().GetProperty(name)!.SetValue(row, value); Render(c); });
        public Task Charge(object c, string name, string price) => _renderer.Dispatcher.InvokeAsync(() => { var row = Activator.CreateInstance(c.GetType().GetNestedType("ItemRow", BindingFlags.NonPublic)!, true)!; foreach (var pair in new Dictionary<string, object> { ["Name"] = name, ["Price"] = price, ["Charge"] = true }) row.GetType().GetProperty(pair.Key)!.SetValue(row, pair.Value); Value<IList>(c, "_items").Add(row); Render(c); });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator { public List<IComponent> Items = []; public IComponent CreateInstance(Type type) { var item = (IComponent)Activator.CreateInstance(type)!; Items.Add(item); return item; } }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(id == "import" ? (T)(object)new Module() : default!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        private sealed class Module : IJSObjectReference { public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!); public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/team");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
