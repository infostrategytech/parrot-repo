using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Shared;
using Parrot.Web.Features.DynamicForms;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.DynamicForms;

// Renders the real form: each kind drawn by its component (DynamicQuestion), the frame around them (QuestionForm),
// and the host a page drops in (DynamicFormHost).
public class DynamicFormRenderTests
{
    private static ServiceProvider Services(IDynamicFormSource? source = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton(new AuroraInterop(Mock.Of<IJSRuntime>()));
        services.AddSingleton<ToastService>();
        services.AddSingleton<UserClock>();
        services.AddLogging();
        services.AddSingleton(source ?? new UnavailableDynamicFormSource());
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters, IDynamicFormSource? source = null) where T : IComponent
    {
        await using var provider = Services(source);
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            await root.QuiescenceTask;
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    private static Task<string> FormAsync(IReadOnlyList<FormQuestion> questions, Dictionary<string, object?>? more = null)
    {
        var parameters = new Dictionary<string, object?> { ["Title"] = "How customers pay you", ["Questions"] = questions, ["SubmitLabel"] = "Looks right" };
        foreach (var (name, value) in more ?? []) parameters[name] = value;
        return RenderAsync<QuestionForm>(parameters);
    }

    [Fact]
    public async Task EachKind_IsDrawnByItsComponent()
    {
        var html = await FormAsync(
        [
            new FormQuestion("one", "Pick one", FormQuestionKind.One, [new("A"), new("B")]),
            new FormQuestion("many", "Pick some", FormQuestionKind.Many, [new("C"), new("D")], AllowOther: true),
            new FormQuestion("yes", "Do you deliver?", FormQuestionKind.YesNo),
            new FormQuestion("words", "What should I never do?", FormQuestionKind.Text),
            new FormQuestion("name", "Your name", FormQuestionKind.Short),
            new FormQuestion("email", "Your email", FormQuestionKind.Email),
            new FormQuestion("site", "Your website", FormQuestionKind.Url),
            new FormQuestion("phone", "Your number", FormQuestionKind.Phone),
            new FormQuestion("fee", "Delivery fee", FormQuestionKind.Money),
            new FormQuestion("days", "Days to deliver", FormQuestionKind.Number),
            new FormQuestion("agree", "I can reply for you on WhatsApp", FormQuestionKind.Consent),
            new FormQuestion("state", "Which state?", FormQuestionKind.Select, [.. Enumerable.Range(1, 8).Select(index => new ChoicePromptOption($"State {index}"))]),
            new FormQuestion("bank", "Which bank?", FormQuestionKind.Search, [.. Enumerable.Range(1, 20).Select(index => new ChoicePromptOption($"Bank {index}"))]),
            new FormQuestion("tone", "How should I sound?", FormQuestionKind.Chips, [new("Friendly"), new("Formal")]),
            new FormQuestion("areas", "Where do you deliver?", FormQuestionKind.Tags),
            new FormQuestion("day", "When do you reopen?", FormQuestionKind.Date),
            new FormQuestion("when", "When is the event?", FormQuestionKind.DateTime),
            new FormQuestion("shots", "Photos of your shop", FormQuestionKind.Photos),
            new FormQuestion("note", "These are the hours customers will see.", FormQuestionKind.Note),
        ]);

        Assert.Contains("role=\"radiogroup\" aria-label=\"Pick one\"", html);
        Assert.Contains("role=\"group\" aria-label=\"Pick some\"", html);
        Assert.Contains("Something else", html);
        Assert.Contains(">Yes<", html);
        Assert.Contains(">No<", html);
        Assert.Contains("<textarea", html);
        Assert.Contains("type=\"email\"", html);
        Assert.Contains("type=\"url\"", html);
        Assert.Contains("type=\"tel\"", html);
        Assert.Contains("+234", html);
        Assert.Contains("₦", html);
        Assert.Contains("inputmode=\"numeric\"", html);
        Assert.Contains("I can reply for you on WhatsApp", html);
        Assert.Contains("select-field", html);
        Assert.Contains("ds-choice-chip", html);
        Assert.Contains("Where do you deliver?", html);
        Assert.Contains("Pick a date", html);
        Assert.Contains("Pick a date and time", html);
        Assert.Contains("ds-photo-picker", html);
        Assert.Contains("role=\"note\"", html);
        Assert.Contains("These are the hours customers will see.", html);
    }

    [Fact]
    public async Task TheFrame_ShowsProgress_Changes_OptionalQuestions_AndTheActions()
    {
        var html = await FormAsync(
            [new FormQuestion("deposit", "Do you take a deposit?", FormQuestionKind.Money) { Required = false }],
            new()
            {
                ["Step"] = 3,
                ["Steps"] = 6,
                ["Changes"] = new FormChanges(["Bank transfer, card or pay on delivery."], ["Bank transfer only."]),
                ["OnDecide"] = EventCallback.Factory.Create<IReadOnlyDictionary<string, FormAnswer>>(new object(), () => { }),
                ["OnSkip"] = EventCallback.Factory.Create<IReadOnlyDictionary<string, FormAnswer>>(new object(), () => { }),
                ["SaveError"] = "That didn't save. Check your connection and try again.",
            });

        Assert.Contains("Step 3 of 6", html);
        Assert.Contains("Adding: </span>Bank transfer, card or pay on delivery.", html);
        Assert.Matches(@"Instead of <s[^>]*>Bank transfer only.</s>", html);
        Assert.Contains("(optional)", html);
        Assert.Contains("Decide for me", html);
        Assert.Contains("Skip for now", html);
        Assert.Contains("Looks right", html);
        Assert.Contains("That didn't save. Check your connection and try again.", html);
        Assert.Contains("Try again", html);
    }

    [Fact]
    public async Task TheRecommendedAnswer_ComesFirst_AndSaysSo()
    {
        var html = await FormAsync([new FormQuestion("pay", "How can customers pay?", FormQuestionKind.One, [new("Card"), new("Bank transfer", "To your business account") { Recommended = true }])]);

        Assert.Contains("Recommended · To your business account", html);
        Assert.True(html.IndexOf("Bank transfer", StringComparison.Ordinal) < html.IndexOf("Card", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APrefilledAnswer_ShowsAsALineToCheck_WithChange()
    {
        var html = await FormAsync(
            [new FormQuestion("fee", "Delivery fee", FormQuestionKind.Money)],
            new() { ["Answers"] = new Dictionary<string, FormAnswer> { ["fee"] = FormAnswer.None with { Number = 2500, Text = "2,500" } }, ["Review"] = true });

        Assert.Contains("₦2,500", html);
        Assert.Contains("Change: Delivery fee", html);
    }

    [Fact]
    public async Task TheHost_SaysPlainly_WhenFormsArentReady()
    {
        var html = await RenderAsync<DynamicFormHost>(new() { ["Flow"] = "agent-setup" });

        Assert.Contains("This step isn't ready yet", html);
    }

    [Fact]
    public async Task TheHost_DrawsTheFirstForm_FromTheBackend()
    {
        var source = new Mock<IDynamicFormSource>();
        source.SetupGet(forms => forms.Available).Returns(true);
        source.Setup(forms => forms.StartAsync(It.IsAny<DynamicFormRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiDynamicFormSource.Read("""{ "form": { "id": "f", "title": "Your job", "submitLabel": "Approve", "allowDecide": true, "questions": [ { "id": "q", "type": "text", "question": "What should I never do?" } ] } }"""));

        var html = await RenderAsync<DynamicFormHost>(new() { ["Flow"] = "agent-setup", ["SubjectId"] = "agent-1" }, source.Object);

        Assert.Contains("Your job", html);
        Assert.Contains("What should I never do?", html);
        Assert.Contains("Approve", html);
        Assert.Contains("Decide for me", html);
        source.Verify(forms => forms.StartAsync(new DynamicFormRequest("agent-setup", "agent-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheHost_SaysWhenTheFirstFormCantBeFetched()
    {
        var source = new Mock<IDynamicFormSource>();
        source.SetupGet(forms => forms.Available).Returns(true);
        source.Setup(forms => forms.StartAsync(It.IsAny<DynamicFormRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        var html = await RenderAsync<DynamicFormHost>(new() { ["Flow"] = "agent-setup" }, source.Object);

        Assert.Contains("We couldn't get the next step.", html);
        Assert.Contains("Try again", html);
    }
}
