using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Team;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Team;
using Parrot.Web.Services;
using TeamPage = global::Parrot.Web.Components.Pages.Dashboard.Team.Team;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Real frontend components and TeamClient, exercised through controlled HTTP rather than demo fixtures.
public sealed class RoleExperienceTests
{
    [Fact]
    public async Task TableLabelsBothOriginsAndKeepsBuiltInsBeforeCustomRoles()
    {
        await using var page = await Page.Team();
        await page.Set(page.Get<TeamPage>(), "_roles", true);
        var html = await page.Html();
        Assert.Contains(">Type</", html); Assert.Contains("Built-in", html); Assert.Contains("Custom", html);
        Assert.True(html.IndexOf("Customer service", StringComparison.Ordinal) < html.IndexOf("Returns helper", StringComparison.Ordinal));
        Assert.All(page.Items<RoleRow>().Where(r => !r.Head), row => Assert.True(row.OnOpen.HasDelegate));
    }

    [Fact]
    public async Task BuiltInDetailsShowEveryGrantedPermissionWithoutEditableControls()
    {
        await using var page = await Page.Details(BuiltIn());
        var html = await page.Html();
        Assert.False(page.Modal.OnSubmit.HasDelegate);
        Assert.Contains("Built-in role. Duplicate it to make your own.", html);
        Assert.Contains("Reply to customers", html); Assert.Contains("Manage tasks", html);
        Assert.DoesNotContain("Edit role", html); Assert.DoesNotContain("type=\"submit\"", html);
        Assert.DoesNotContain("role=\"checkbox\"", html); Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task CustomDetailsAreReadableBeforeEditingAndKeepUnlistedPermissionsVisible()
    {
        var role = Custom() with { Permissions = ["tasks.manage", "existing.unlisted"] };
        await using var page = await Page.Details(role);
        var html = await page.Html();
        Assert.Contains("Custom role. Created for your business.", html); Assert.Contains("Manage tasks", html);
        Assert.Contains("existing.unlisted", html); Assert.Contains("Edit role", html); Assert.Contains("2 people use this role.", html);
        Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task BuiltInNameOpensDetailsAndNeverTheEditor()
    {
        await using var page = await Page.Team();
        await page.Call(page.Get<TeamPage>(), "OpenRole", BuiltIn());
        Assert.Equal(BuiltIn().Id, page.Get<RoleDetails>().Role.Id);
        Assert.Null(page.Get<RoleDetails>().Error);
        Assert.Empty(page.Items<RoleEditor>()); Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task CustomDetailsCanOpenTheirEditor()
    {
        await using var page = await Page.Team();
        await page.Call(page.Get<TeamPage>(), "OpenRoleDetails", Custom());
        await page.Invoke(() => page.Get<RoleDetails>().OnEdit.InvokeAsync());
        Assert.Equal(Custom().Id, page.Get<RoleEditor>().Role?.Id); Assert.True(page.Modal.OnSubmit.HasDelegate);
        Assert.Contains("Changes affect 2 people", page.Modal.Subtitle);
    }

    [Fact]
    public async Task DuplicateUsesTheExistingEndpointAndImmediatelyOpensTheReturnedCustomRole()
    {
        await using var page = await Page.Team();
        await page.Call(page.Get<TeamPage>(), "OpenRoleDetails", BuiltIn());
        await page.Invoke(() => page.Get<RoleDetails>().OnDuplicate.InvokeAsync());
        var request = Assert.Single(page.Http.Writes);
        Assert.Equal("POST", request.Method); Assert.Equal("/api/team/roles/conversation-manager/duplicate", request.Path);
        var editor = page.Get<RoleEditor>();
        Assert.Equal("controlled-copy", editor.Role?.Id); Assert.True(editor.Role?.IsCustom); Assert.True(editor.CopyCreated);
        Assert.Contains("Your copy has been created", page.Modal.Subtitle);
        await page.Call(editor, "RequestCloseAsync");
        Assert.Contains(page.Http.Roles, role => role.Id == "controlled-copy");
        Assert.DoesNotContain(page.Http.Writes, write => write.Method == "DELETE"); // Cancel does not pretend the API copy never existed.
    }

    [Fact]
    public async Task FailedDuplicateLeavesDetailsOpenAndCanRetry()
    {
        await using var page = await Page.Team(); page.Http.FailDuplicate = true;
        await page.Call(page.Get<TeamPage>(), "OpenRoleDetails", BuiltIn());
        await page.Invoke(() => page.Get<RoleDetails>().OnDuplicate.InvokeAsync());
        Assert.Empty(page.Items<RoleEditor>()); Assert.False(page.Get<RoleDetails>().Busy);
        Assert.Contains("couldn't duplicate", await page.Html());
        page.Http.FailDuplicate = false;
        await page.Invoke(() => page.Get<RoleDetails>().OnDuplicate.InvokeAsync());
        Assert.Equal("controlled-copy", page.Get<RoleEditor>().Role?.Id); Assert.Equal(2, page.Http.Writes.Count);
    }

    [Fact]
    public async Task DuplicationGuardRejectsRepeatedActionsAndPreventsDeletionDuringTheRequest()
    {
        await using var page = await Page.Team(); var team = page.Get<TeamPage>();
        await page.Set(team, "_duplicating", true);
        await page.Call(team, "DuplicateRoleAsync", BuiltIn());
        await page.Call(team, "OpenRole", Custom());
        await page.Call(team, "RequestDeleteRole", Custom() with { Holders = 0 });
        Assert.Empty(page.Items<RoleEditor>()); Assert.Empty(page.Http.Writes);
        Assert.Null(page.Value<TeamRole?>(team, "_deleting"));
    }

    [Fact]
    public async Task AssignedCustomRolesExplainWhyDeleteIsDisabled()
    {
        await using var page = await Page.Row(Custom());
        await page.Call(page.Get<ActionMenu>(), "Toggle");
        var delete = Assert.Single(page.Items<ActionMenuItem>().Where(item => item.Label == "Delete role"));
        Assert.True(delete.Disabled); Assert.True(delete.Destructive);
        Assert.Equal("Reassign people using this role before deleting it.", page.Get<ActionMenu>().FooterText);
        Assert.Contains(page.Get<ActionMenu>().FooterText!, await page.Html());
    }

    [Fact]
    public async Task BuiltInsHaveNoDeleteOrEditActionsAndAssignedRolesCannotReachDeleteConfirmation()
    {
        await using var row = await Page.Row(BuiltIn());
        await row.Call(row.Get<ActionMenu>(), "Toggle");
        Assert.DoesNotContain(row.Items<ActionMenuItem>(), item => item.Label is "Edit role" or "Delete role");
        await using var page = await Page.Team(); var team = page.Get<TeamPage>();
        await page.Call(team, "RequestDeleteRole", BuiltIn()); Assert.Null(page.Value<TeamRole?>(team, "_deleting"));
        await page.Call(team, "RequestDeleteRole", Custom()); Assert.Null(page.Value<TeamRole?>(team, "_deleting"));
        await page.Call(team, "RequestDeleteRole", Custom() with { Holders = 0 }); Assert.Equal(Custom().Id, page.Value<TeamRole>(team, "_deleting").Id);
    }

    [Fact]
    public async Task EditorUsesContentModalNativeSubmissionAndFocusesMissingFields()
    {
        await using var page = await Page.Editor(); var editor = page.Get<RoleEditor>();
        Assert.True(page.Modal.OnSubmit.HasDelegate);
        Assert.DoesNotContain("content-modal--compact-heading", await page.Html());
        var html = await page.Html(); var form = Regex.Match(html, "<form id=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(form); Assert.Contains($"form=\"{form}\"", html);
        await page.Call(editor, "SaveAsync"); Assert.Equal("role-name", page.Modal.InitialFocusId);
        await page.Set(editor, "_name", "Controlled role"); await page.Call(editor, "SaveAsync");
        Assert.Equal("role-editor-permissions", page.Modal.InitialFocusId); Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task CreateAndEditKeepTheExistingApiPayloadsAndCatalogPermissionOrder()
    {
        await using var page = await Page.Editor(); var editor = page.Get<RoleEditor>();
        await page.Set(editor, "_name", " Controlled role ");
        await page.Call(editor, "Toggle", "tasks.manage", CheckboxState.Checked);
        await page.Call(editor, "Toggle", "conversations.reply", CheckboxState.Checked);
        await page.Call(editor, "SaveAsync");
        var create = Assert.Single(page.Http.Writes);
        Assert.Equal("POST", create.Method); Assert.Equal("/api/team/roles", create.Path);
        Assert.Equal("Controlled role", create.Body.GetProperty("name").GetString());
        Assert.Equal(new[] { "conversations.reply", "tasks.manage" }, create.Body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(1, page.Saved);

        await using var edited = await Page.Editor(Custom()); await edited.Call(edited.Get<RoleEditor>(), "SaveAsync");
        var update = Assert.Single(edited.Http.Writes);
        Assert.Equal("PUT", update.Method); Assert.Equal("/api/team/roles/controlled-custom", update.Path);
    }

    [Fact]
    public async Task PartialCatalogDoesNotSilentlyRemoveExistingPermissions()
    {
        var role = Custom() with { Permissions = ["tasks.manage", "existing.unlisted"] };
        await using var page = await Page.Editor(role);
        Assert.Contains("They will be kept unchanged", await page.Html());
        await page.Call(page.Get<RoleEditor>(), "SaveAsync");
        Assert.Contains("existing.unlisted", Assert.Single(page.Http.Writes).Body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task BuiltInCannotBeSavedEvenIfPassedDirectlyToEditor()
    {
        await using var page = await Page.Editor(BuiltIn()); var editor = page.Get<RoleEditor>();
        await page.Call(editor, "Toggle", "tasks.manage", CheckboxState.Unchecked);
        await page.Call(editor, "SaveAsync");
        Assert.Empty(page.Http.Writes); Assert.DoesNotContain("type=\"submit\"", await page.Html());
        Assert.DoesNotContain("Role name", await page.Html());
    }

    [Fact]
    public async Task FailedRoleSaveRetainsEditsAndAllowsRetry()
    {
        await using var page = await Page.Editor(Custom()); var editor = page.Get<RoleEditor>();
        await page.Set(editor, "_name", "Controlled renamed role"); page.Http.FailSave = true; await page.Call(editor, "SaveAsync");
        Assert.Contains("couldn't save", await page.Html()); Assert.False(page.Modal.Busy); Assert.Equal(0, page.Saved);
        Assert.Equal("Controlled renamed role", page.Value<string>(editor, "_name"));
        page.Http.FailSave = false; await page.Call(editor, "SaveAsync"); Assert.Equal(1, page.Saved);
    }

    [Fact]
    public async Task ClosingProtectsMaterialEditsButNotAnUntouchedRole()
    {
        await using var page = await Page.Editor(Custom()); var editor = page.Get<RoleEditor>();
        await page.Call(editor, "RequestCloseAsync"); Assert.Equal(1, page.Closed);
        await page.Set(editor, "_name", "New controlled name"); await page.Call(editor, "RequestCloseAsync");
        Assert.True(page.Value<bool>(editor, "_discard")); Assert.Equal(1, page.Closed);
        await page.Call(editor, "KeepEditing"); Assert.Equal("New controlled name", page.Value<string>(editor, "_name"));
        await page.Call(editor, "DiscardAsync"); Assert.Equal(2, page.Closed); Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task BusyEditorBlocksFurtherSubmissionsAndExit()
    {
        await using var page = await Page.Editor(Custom()); var editor = page.Get<RoleEditor>();
        await page.Set(editor, "_saving", true); await page.Call(editor, "SaveAsync"); await page.Call(editor, "RequestCloseAsync");
        Assert.True(page.Modal.Busy); Assert.Empty(page.Http.Writes); Assert.Equal(0, page.Closed);
    }

    [Fact]
    public async Task PendingInvitesExplainAndBlockDeletionEvenWithoutActiveHolders()
    {
        await using var page = await Page.Team(); var team = page.Get<TeamPage>();
        page.Http.Invites.Add(new(Guid.NewGuid(), "Controlled invite", "invite@example.test",
            [new(null, Custom().Id, Custom().Name, true, [])], DateTime.UtcNow, DateTime.UtcNow.AddDays(7)));
        await page.Call(team, "LoadAsync");
        await page.Call(team, "RequestDeleteRole", Custom() with { Holders = 0 });
        Assert.Null(page.Value<TeamRole?>(team, "_deleting"));

        await using var row = await Page.Row(Custom() with { Holders = 0 }, pendingInvites: true);
        await row.Call(row.Get<ActionMenu>(), "Toggle");
        var delete = Assert.Single(row.Items<ActionMenuItem>().Where(item => item.Label == "Delete role"));
        Assert.True(delete.Disabled);
        Assert.Equal("Withdraw pending invites using this role before deleting it.", row.Get<ActionMenu>().FooterText);
        Assert.Contains(row.Get<ActionMenu>().FooterText!, await row.Html());
        Assert.Empty(page.Http.Writes);
    }

    [Fact]
    public async Task DeleteExplanationUsesTheAccessibleFooterWithoutRestoringMultilineMenuItems()
    {
        await using var page = await Page.Row(Custom());
        await page.Call(page.Get<ActionMenu>(), "Toggle");
        var html = await page.Html();
        var id = Regex.Match(html, "aria-describedby=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(id);
        Assert.Contains($"class=\"ds-action-menu__footer\" id=\"{id}\"", html);
        Assert.DoesNotContain("ds-action-menu-item__description", html);
        Assert.Null(typeof(ActionMenuItem).GetProperty("Description"));
        Assert.NotNull(typeof(ActionMenuItem).GetProperty("Destructive"));
        Assert.NotNull(typeof(ActionMenu).GetProperty("Dense"));
    }

    private static TeamRole BuiltIn() => new("conversation-manager", "Conversation Manager", false, ["conversations.reply", "tasks.manage"], 0);
    private static TeamRole Custom() => new("controlled-custom", "Returns helper", true, ["tasks.manage"], 2);
    private sealed record Request(string Method, string Path, JsonElement Body);
    private sealed class Http : HttpMessageHandler
    {
        public bool FailDuplicate, FailSave;
        public List<TeamRole> Roles = [Custom(), BuiltIn()]; // Deliberately unordered controlled HTTP response.
        public List<TeamInvite> Invites = [];
        public TeamCatalog Catalog => new(Roles.ToArray(), [new("conversations.reply", "Reply to customers"), new("tasks.manage", "Manage tasks")], []);
        public List<Request> Writes = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = path == "/api/team/roles" ? JsonContent.Create(Catalog)
                    : JsonContent.Create(new TeamSnapshot([new("controlled-owner", "Controlled owner", "owner@example.test", true, [], DateTime.UtcNow)], Invites.ToArray(), [new("controlled-workspace", "Controlled workspace", null)])) };
            var body = request.Content is null ? JsonSerializer.SerializeToElement(new { }) : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            Writes.Add(new(request.Method.Method, path, body));
            if (path.EndsWith("/duplicate", StringComparison.Ordinal))
            {
                if (FailDuplicate) throw new HttpRequestException("Controlled failure");
                var role = new TeamRole("controlled-copy", "Controlled role copy", true, BuiltIn().Permissions, 0); Roles.Add(role);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(role) };
            }
            if (FailSave) throw new HttpRequestException("Controlled failure");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new TeamRole("controlled-created", body.GetProperty("name").GetString()!, true,
                body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray(), 0)) };
        }
    }

    private sealed class Page : IAsyncDisposable
    {
        public Http Http = new(); public int Saved, Closed;
        private readonly Capture _capture = new();
        private ServiceProvider _services = null!; private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _html;
        public IEnumerable<T> Items<T>() => _capture.Items.OfType<T>();
        public T Get<T>() => Items<T>().Last();
        public ContentModal Modal => Items<ContentModal>().Last(m => m.Open);
        public static async Task<Page> Team() { var p = new Page(); await p.Open<TeamPage>([]); return p; }
        public static async Task<Page> Details(TeamRole role) { var p = new Page(); await p.Open<RoleDetails>(new() { ["Role"] = role, ["Catalog"] = p.Http.Catalog }); return p; }
        public static async Task<Page> Row(TeamRole role, bool pendingInvites = false) { var p = new Page(); await p.Open<RoleRow>(new() { ["Role"] = role, ["HasPendingInvites"] = pendingInvites }); return p; }
        public static async Task<Page> Editor(TeamRole? role = null)
        {
            var p = new Page(); await p.Open<RoleEditor>(new() { ["Role"] = role, ["Catalog"] = p.Http.Catalog,
                ["OnSaved"] = EventCallback.Factory.Create(p, () => p.Saved++), ["OnCancel"] = EventCallback.Factory.Create(p, () => p.Closed++) }); return p;
        }
        private async Task Open<T>(Dictionary<string, object?> parameters) where T : IComponent
        {
            var js = new Js(); var auth = new AuthState();
            auth.SetUser(new AuthResponse { UserId = "controlled-owner", Role = "Owner", Token = "controlled", Email = "owner@example.test" });
            _services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new TestNavigation()).AddSingleton(auth).AddSingleton<IJSRuntime>(js).AddSingleton<AuroraInterop>().AddSingleton<ToastService>()
                .AddSingleton(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider())).AddSingleton<TokenStorageService>()
                .AddSingleton(new HttpClient(Http) { BaseAddress = new Uri("http://localhost/") }).AddSingleton<TeamClient>().AddSingleton<IComponentActivator>(_capture).BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
            _html = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_html.ToHtmlString()));
        public T Value<T>(object item, string name) => (T)item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
        private static void Render(object item) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
        public Task Set(object item, string name, object? value) => _renderer.Dispatcher.InvokeAsync(() => { item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, value); Render(item); });
        public Task Call(object item, string name, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () => { var result = item.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, args); if (result is Task task) await task; Render(item); });
        public Task Invoke(Func<Task> action) => _renderer.Dispatcher.InvokeAsync(action);
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator { public List<IComponent> Items = []; public IComponent CreateInstance(Type type) { var item = (IComponent)Activator.CreateInstance(type)!; Items.Add(item); return item; } }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/team");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
