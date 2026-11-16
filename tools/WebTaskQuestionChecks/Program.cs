using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.TaskRequests;
using Parrot.Web.Features.Dashboard.TaskRequests;

var checks = 0;
void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); checks++; }
var me = new TaskPerson("viewer", "Viewer");
var colleague = new TaskPerson("colleague", "Controlled teammate");
var question = new TaskQuestion("q", "task", "chat", "Is black available?", me, colleague, 1, DateTime.UtcNow);
Check(question.Waits(colleague.Id) && !question.Waits(me.Id), "A question waits on its recipient.");
question = question with { Answer = "Yes, in black.", Revision = 2 };
Check(question.Waits(me.Id) && !question.Waits(colleague.Id), "An answer returns to its asker.");
Check(!(question with { Closed = true }).Waits(me.Id), "Closing a consultation removes its attention state.");
var command = new QuestionCommand(Guid.NewGuid(), QuestionAction.Ask, "task", Text: "Is black available?", PersonId: colleague.Id);
Check(TaskQuestionRules.Validate(command) is null, "A complete Ask command validates.");
Check(TaskQuestionRules.Validate(command with { RequestId = Guid.Empty }) is not null, "A request identity is required.");
Check(TaskQuestionRules.Validate(command with { TaskId = " " }) is not null, "A parent task is required.");
Check(TaskQuestionRules.Validate(command with { PersonId = null }) is not null, "Ask requires a recipient.");
Check(TaskQuestionRules.Validate(command with { Text = " " }) is not null, "Ask requires a question.");
Check(TaskQuestionRules.Validate(command with { Text = new('x', 1001) }) is not null, "Questions are bounded.");
Check(TaskQuestionRules.Validate(command with { Text = new('x', 1000) }) is null, "The documented text limit is inclusive.");
var answer = new QuestionCommand(Guid.NewGuid(), QuestionAction.Answer, "task", "q", 1, "Yes.");
Check(TaskQuestionRules.Validate(answer) is null, "Answer carries identity and revision.");
Check(TaskQuestionRules.Validate(answer with { Text = " " }) is not null, "Blank answers cannot send.");
Check(TaskQuestionRules.Validate(answer with { QuestionId = null }) is not null, "An answer targets a question.");
Check(TaskQuestionRules.Validate(answer with { Revision = 0 }) is not null, "An answer needs a current revision.");
Check(TaskQuestionRules.Validate(answer with { Action = QuestionAction.Close, Text = null }) is null, "Close needs no invented answer text.");
Check(!TaskWords.Kinds.Contains(TaskKind.Question), "Unsupported Question task creation is not offered to the current backend.");
Check(TaskWords.Label(TaskKind.Question) == "Question", "Incoming Question tasks still have a readable label.");
var drafts = new TaskReplyDraft();
drafts.Set("one", "Editable answer");
Check(drafts.Take("two") is null, "An answer is handed only to its own conversation.");
Check(drafts.Take("one") == "Editable answer", "Task-to-chat handoff preserves answer text.");
Check(drafts.Take("one") is null, "The handoff is consumed once, not repeatedly appended.");
ITaskQuestions unavailable = new UnavailableTaskQuestions();
Check(!unavailable.Available && (await unavailable.ListAsync()).Count == 0, "Real mode does not fabricate questions.");
Check((await unavailable.PeopleAsync("task")).Count == 0, "Unavailable questions do not fabricate recipients.");
try { await unavailable.ApplyAsync(command); throw new Exception("Unexpected question write."); }
catch (NotSupportedException) { checks++; }

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IJSRuntime, NoBrowser>();
services.AddScoped<AuroraInterop>();
services.AddScoped<ToastService>();
services.AddScoped<Parrot.Web.Services.UserClock>();
services.AddScoped<TaskReplyDraft>();
services.AddSingleton<NavigationManager, ControlledNavigation>();
var store = new ControlledTasks(me, colleague);
services.AddSingleton<ITaskStore>(store);
services.AddSingleton(unavailable);
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
var passHtml = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PassOnHost>()).ToHtmlString());
Check(passHtml.Contains("Pass on") && passHtml.Contains("Controlled teammate"), "Whole-task handoff renders with the existing task-team contract.");
Check(!passHtml.Contains("Answer a question") && !passHtml.Contains("Send answer"), "Backend-dependent question actions stay unavailable in real mode.");
var questionHtml = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TaskQuestions>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TaskQuestions.TaskId)] = store.Item.Id }))).ToHtmlString());
Check(!questionHtml.Contains("Questions for your team"), "No misleading consultation UI is drawn when its service is unavailable.");
Check(store.Writes == 0 && store.Item.State == TaskState.InProgress && store.Item.Total == 250m, "Rendering or opening a handoff never changes task money or state.");
Console.WriteLine($"{checks} web Inbox/task-handoff contract and real-mode render checks passed; no sample data or external API used.");

sealed class PassOnHost : ComponentBase
{
    [Inject] public ITaskStore Store { get; set; } = default!;
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<SectionOutlet>(0); builder.AddAttribute(1, "SectionName", "web-task-question-modal"); builder.CloseComponent();
        builder.OpenComponent<PassOnDialog>(2); builder.AddAttribute(3, "Open", true); builder.AddAttribute(4, "Item", ((ControlledTasks)Store).Item); builder.CloseComponent();
    }
}
sealed class NoBrowser : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static checks must not invoke a browser.");
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
}
sealed class ControlledNavigation : NavigationManager
{
    public ControlledNavigation() { Initialize("https://controlled.test/", "https://controlled.test/dashboard/inbox"); }
    protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Rendering must not navigate.");
}
sealed class ControlledTasks(TaskPerson viewer, TaskPerson colleague) : ITaskStore
{
    public TaskRequest Item { get; } = new() { Id = "task", Title = "Confirm this order", Kind = TaskKind.Order, RaisedBy = "Agent", RaisedAt = DateTime.UtcNow,
        Customer = new("Customer", "whatsapp", ConversationId: "chat"), Assignee = viewer, State = TaskState.InProgress, Total = 250m };
    public int Writes { get; private set; }
    public bool Available => true;
    public TaskPerson Me => viewer;
    public event Action? Changed { add { } remove { } }
    public Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskRequest>>([Item]);
    public Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskPerson>>([viewer, colleague]);
    public Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskCustomer>>([]);
    public Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>([]);
    private Task RejectWrite() { Writes++; throw new InvalidOperationException("Rendering must not write."); }
    public Task<TaskRequest> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default) { RejectWrite(); throw new InvalidOperationException(); }
    public Task EditAsync(string id, TaskDraft draft, CancellationToken cancellationToken = default) => RejectWrite();
    public Task SetStateAsync(string id, TaskState state, CancellationToken cancellationToken = default) => RejectWrite();
    public Task SetUrgentAsync(string id, bool urgent, CancellationToken cancellationToken = default) => RejectWrite();
    public Task SetDueAsync(string id, DateTime? dueAt, bool hasTime, CancellationToken cancellationToken = default) => RejectWrite();
    public Task AssignAsync(string id, TaskPerson? assignee, CancellationToken cancellationToken = default) => RejectWrite();
    public Task PostUpdateAsync(string id, string text, CancellationToken cancellationToken = default) => RejectWrite();
}
