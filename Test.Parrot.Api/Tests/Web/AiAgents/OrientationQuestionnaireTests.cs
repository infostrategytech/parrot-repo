using System.Text.Json;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

// The forms the AI service sends during orientation, read for Teach your AI's questionnaire card.
// The sample is the service's own (Omni-AI pkg/api/dto/agent_behaviour.json).
public class OrientationQuestionnaireTests
{
    private const string AgentBehaviour = """
        {
          "id": "agent-behaviour",
          "title": "How your AI should work",
          "subtitle": "Choose how it communicates and when it should ask for help.",
          "submitLabel": "Save behaviour",
          "allowDecide": true,
          "step": 3,
          "steps": 4,
          "questions": [
            {"id": "tone", "type": "one", "header": "Reply style", "question": "How should your AI sound?",
             "options": [{"label": "Warm and friendly", "description": "Relaxed, helpful and conversational"},
                         {"label": "Professional", "description": "Clear, calm and business-focused"},
                         {"label": "Playful", "description": "Energetic and expressive"}],
             "required": true, "value": "Warm and friendly"},
            {"id": "replyLength", "type": "select", "question": "How long should its replies usually be?", "placeholder": "Reply length",
             "options": [{"label": "Short", "description": "One or two direct sentences"},
                         {"label": "Balanced", "description": "Enough detail to answer clearly"},
                         {"label": "Detailed", "description": "More explanation when helpful"}],
             "required": true, "value": "Balanced"},
            {"id": "handoff", "type": "yesno", "question": "Should it hand over when a customer asks for a person?", "required": true, "value": "true"},
            {"id": "avoid", "type": "text", "question": "Is there anything it should never promise?",
             "placeholder": "For example, never guarantee same-day delivery", "required": false},
            {"id": "approval", "type": "consent", "question": "Can this agent use these instructions?",
             "consentLabel": "Use these approved instructions when replying", "required": true, "value": "true"}
          ]
        }
        """;

    [Fact]
    public void TheServicesSampleReadsWithEveryQuestion()
    {
        OrientationQuestionnaire form = Read(AgentBehaviour)!;

        Assert.Equal("How your AI should work", form.Title);
        Assert.Equal("Save behaviour", form.SubmitLabel);
        Assert.True(form.AllowDecide);
        Assert.Equal((3, 4), (form.Step, form.Steps));
        Assert.Equal(["one", "select", "yesno", "text", "consent"], form.Questions.Select(q => q.Type));
        Question tone = form.Questions[0];
        Assert.Equal("How should your AI sound?", tone.Text);
        Assert.Equal("Relaxed, helpful and conversational", tone.Options[0].Description);
        Assert.Equal("Use these approved instructions when replying", form.Questions[4].ConsentLabel);
        Assert.False(form.Questions[3].Required);
    }

    [Fact]
    public void ItStartsFromTheAisSuggestionsAndNeedsNothingMore()
    {
        OrientationQuestionnaire form = Read(AgentBehaviour)!;

        Dictionary<string, object?> answers = form.Suggested();

        Assert.Equal("Warm and friendly", answers["tone"]);
        Assert.Equal("Balanced", answers["replyLength"]);
        Assert.Equal(true, answers["handoff"]);
        Assert.Null(answers["avoid"]);
        Assert.Equal(true, answers["approval"]);
        Assert.Empty(form.Missing(answers));
    }

    [Fact]
    public void RequiredQuestionsMustBeAnsweredAndConsentGiven()
    {
        OrientationQuestionnaire form = Read(AgentBehaviour)!;
        Dictionary<string, object?> answers = form.Suggested();

        answers["tone"] = null;
        answers["handoff"] = null;
        answers["approval"] = false;

        Assert.Equal(["tone", "handoff", "approval"], form.Missing(answers));
    }

    [Fact]
    public void ASuggestionThatIsNotAnOptionIsLeftUnpicked()
    {
        OrientationQuestionnaire form = Read("""{"questions":[{"id":"tone","type":"one","options":["Calm"],"value":"Loud","required":true}]}""")!;

        Assert.Null(form.Suggested()["tone"]);
        Assert.Equal(["tone"], form.Missing(form.Suggested()));
        Assert.Equal("A few questions from your AI", form.Title);
    }

    [Theory]
    [InlineData("""{"id":"x","type":"rating","question":"How sure are you?"}""")]
    [InlineData("""{"id":"x","type":"one","question":"Pick one","options":[]}""")]
    public void AQuestionThisScreenCannotShowIsAskedInWords(string question)
    {
        OrientationQuestionnaire form = Read($$"""{"questions":[{{question}}]}""")!;

        Assert.Equal(OrientationQuestionnaire.Text, Assert.Single(form.Questions).Type);
    }

    [Fact]
    public void SeveralPicksStartFromTheSuggestedList()
    {
        OrientationQuestionnaire form = Read("""{"questions":[{"id":"days","type":"many","options":["Mon","Tue","Wed"],"value":["Mon","Wed"],"required":true}]}""")!;

        Assert.Equal(["Mon", "Wed"], (List<string>)form.Suggested()["days"]!);
        Assert.Equal(["days"], form.Missing(new Dictionary<string, object?> { ["days"] = new List<string>() }));
    }

    [Theory]
    [InlineData("""{"title":"Nothing to ask","questions":[]}""")]
    [InlineData("""{"title":"No questions"}""")]
    [InlineData("""{"questions":[{"type":"text","question":"No id"}]}""")]
    [InlineData("[]")]
    public void AFormWithNothingToAskIsNotShown(string json)
    {
        Assert.Null(Read(json));
    }

    [Fact]
    public void OnTheDesignedCardEachQuestionTakesItsControl()
    {
        IReadOnlyList<FormQuestion> form = Read(AgentBehaviour)!.AsForm();

        Assert.Equal([FormQuestionKind.One, FormQuestionKind.One, FormQuestionKind.One, FormQuestionKind.Text, FormQuestionKind.Many], form.Select(q => q.Kind));
        Assert.True(form[0].AllowOther);
        Assert.Equal("Relaxed, helpful and conversational", form[0].Options![0].Description);
        Assert.Equal(["Yes", "No"], form[2].Options!.Select(o => o.Label));
        Assert.Equal(["Use these approved instructions when replying"], form[4].Options!.Select(o => o.Label));
    }

    [Fact]
    public void TheCardStartsFromTheSuggestionsAndSendsTheServicesShape()
    {
        OrientationQuestionnaire questionnaire = Read(AgentBehaviour)!;
        IReadOnlyDictionary<string, FormAnswer> start = questionnaire.SuggestedForm();
        Assert.Equal(["Warm and friendly"], start["tone"].Picked);
        Assert.Equal(["Yes"], start["handoff"].Picked);

        Dictionary<string, FormAnswer> picked = new(start)
        {
            ["tone"] = new FormAnswer([], "Calm and precise"),
            ["handoff"] = new FormAnswer(["No"]),
            ["avoid"] = new FormAnswer([], "  same-day delivery  "),
        };
        Dictionary<string, object?> sent = questionnaire.FromForm(picked);

        Assert.Equal("Calm and precise", sent["tone"]);
        Assert.Equal("Balanced", sent["replyLength"]);
        Assert.Equal(false, sent["handoff"]);
        Assert.Equal("same-day delivery", sent["avoid"]);
        Assert.Equal(true, sent["approval"]);
        Assert.Empty(questionnaire.Missing(sent));

        picked["approval"] = FormAnswer.None;
        Assert.Equal(["approval"], questionnaire.Missing(questionnaire.FromForm(picked)));
    }

    private static OrientationQuestionnaire? Read(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return OrientationQuestionnaire.Read(doc.RootElement.Clone());
    }
}
