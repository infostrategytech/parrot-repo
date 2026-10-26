using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Features.DynamicForms;

namespace Test.Parrot.Api.Tests.Web.DynamicForms;

// What the backend sends (dynamic-form-schema.json) is read into the form the app draws; one mistake never stops it.
public class DynamicFormReaderTests
{
    // The example in dynamic-form-components.md, section 3.
    private const string Payments = """
        {
          "id": "payments",
          "title": "How customers pay you",
          "subtitle": "Change anything that isn't right.",
          "submitLabel": "Looks right",
          "allowDecide": true,
          "step": 3,
          "steps": 6,
          "questions": [
            {
              "id": "methods",
              "type": "many",
              "header": "Payments",
              "question": "How can customers pay?",
              "options": [
                { "label": "Card", "description": "Online or with a POS" },
                { "label": "Bank transfer", "description": "To your business account", "recommended": true },
                { "label": "Pay on delivery", "description": "Cash or transfer at the door" }
              ],
              "allowOther": true,
              "value": { "picked": ["Bank transfer"] }
            },
            {
              "id": "deposit",
              "type": "money",
              "question": "Do you take a deposit before an order?",
              "helper": "Leave it empty if you don't.",
              "required": false
            }
          ]
        }
        """;

    [Fact]
    public void TheDocumentedExample_IsReadWhole()
    {
        var read = DynamicFormReader.Read(Payments);

        var form = Assert.IsType<DynamicForm>(read.Form);
        Assert.Empty(read.Problems);
        Assert.Equal("payments", form.Id);
        Assert.Equal("How customers pay you", form.Title);
        Assert.Equal("Looks right", form.SubmitLabel);
        Assert.True(form.AllowDecide);
        Assert.False(form.AllowSkip);
        Assert.Equal((3, 6), (form.Step, form.Steps));

        var methods = form.Questions[0];
        Assert.Equal(FormQuestionKind.Many, methods.Kind);
        Assert.Equal("Payments", methods.Header);
        Assert.True(methods.AllowOther);
        Assert.True(methods.Required);
        Assert.Single(methods.Options!, option => option.Recommended && option.Label == "Bank transfer");
        Assert.Equal(["Bank transfer"], form.Prefilled["methods"].Picked);

        var deposit = form.Questions[1];
        Assert.Equal(FormQuestionKind.Money, deposit.Kind);
        Assert.False(deposit.Required);
        Assert.Equal("Leave it empty if you don't.", deposit.Helper);
        Assert.False(form.Prefilled.ContainsKey("deposit"));
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("file")]
    [InlineData("time")]
    [InlineData("daterange")]
    [InlineData("address")]
    [InlineData("list")]
    [InlineData("hologram")]
    public void ATypeNotDrawnYet_IsAskedAsText_KeepingWhatWasAsked(string type)
    {
        var read = DynamicFormReader.Read($$"""{ "id": "f", "title": "T", "submitLabel": "Save", "questions": [ { "id": "q", "type": "{{type}}", "question": "When are you open?", "helper": "As you'd say it.", "placeholder": "Mon to Fri, 9 to 5" } ] }""");

        var question = Assert.Single(read.Form!.Questions);
        Assert.Equal(FormQuestionKind.Text, question.Kind);
        Assert.Equal(type, question.RequestedType);
        Assert.Equal("When are you open?", question.Question);
        Assert.Equal("As you'd say it.", question.Helper);
        Assert.Equal("Mon to Fri, 9 to 5", question.Placeholder);
        Assert.Contains(read.Problems, problem => problem.Contains(type));
    }

    [Fact]
    public void EveryReadyType_IsDrawnAsItsOwnKind()
    {
        var types = new Dictionary<string, FormQuestionKind>
        {
            ["one"] = FormQuestionKind.One, ["many"] = FormQuestionKind.Many, ["text"] = FormQuestionKind.Text, ["short"] = FormQuestionKind.Short,
            ["email"] = FormQuestionKind.Email, ["url"] = FormQuestionKind.Url, ["phone"] = FormQuestionKind.Phone, ["money"] = FormQuestionKind.Money,
            ["number"] = FormQuestionKind.Number, ["yesno"] = FormQuestionKind.YesNo, ["consent"] = FormQuestionKind.Consent, ["select"] = FormQuestionKind.Select,
            ["search"] = FormQuestionKind.Search, ["chips"] = FormQuestionKind.Chips, ["tags"] = FormQuestionKind.Tags, ["date"] = FormQuestionKind.Date,
            ["datetime"] = FormQuestionKind.DateTime, ["photos"] = FormQuestionKind.Photos, ["note"] = FormQuestionKind.Note,
        };
        foreach (var (type, kind) in types)
        {
            var read = DynamicFormReader.Read($$"""{ "id": "f", "title": "T", "submitLabel": "Save", "questions": [ { "id": "q", "type": "{{type}}", "question": "Q", "options": [ { "label": "A" }, { "label": "B" } ] } ] }""");
            Assert.Equal(kind, Assert.Single(read.Form!.Questions).Kind);
        }
    }

    [Fact]
    public void AChoiceWithoutOptions_IsAskedAsText()
    {
        var read = DynamicFormReader.Read("""{ "id": "f", "title": "T", "submitLabel": "Save", "questions": [ { "id": "q", "type": "select", "question": "Which state?" } ] }""");

        var question = Assert.Single(read.Form!.Questions);
        Assert.Equal(FormQuestionKind.Text, question.Kind);
        Assert.Equal("select", question.RequestedType);
    }

    [Fact]
    public void QuestionsWithoutAnIdOrWords_OrWithAnIdUsedTwice_AreLeftOut()
    {
        var read = DynamicFormReader.Read("""
            { "id": "f", "title": "T", "submitLabel": "Save", "questions": [
              { "type": "text", "question": "No id" },
              { "id": "a", "type": "text" },
              { "id": "b", "type": "text", "question": "Kept" },
              { "id": "b", "type": "text", "question": "Twice" }
            ] }
            """);

        Assert.Equal(["Kept"], read.Form!.Questions.Select(question => question.Question));
        Assert.Equal(3, read.Problems.Count);
    }

    [Fact]
    public void StylingAndUnknownFields_AreIgnored()
    {
        var read = DynamicFormReader.Read("""{ "id": "f", "title": "T", "submitLabel": "Save", "color": "#ff0000", "css": "x", "questions": [ { "id": "q", "type": "short", "question": "Your name", "font": "Comic Sans", "style": { "color": "red" } } ] }""");

        Assert.Equal(FormQuestionKind.Short, Assert.Single(read.Form!.Questions).Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "id": "f", "title": "T", "submitLabel": "Save", "questions": [] }""")]
    [InlineData("""{ "id": "f", "title": "T", "submitLabel": "Save" }""")]
    public void AFormWithNothingToShow_CantBeDrawn_AndSaysWhy(string json)
    {
        var read = DynamicFormReader.Read(json);

        Assert.Null(read.Form);
        Assert.NotEmpty(read.Problems);
    }

    [Fact]
    public void ALaterVersion_IsStillRead_AndNoted()
    {
        var read = DynamicFormReader.Read("""{ "version": 2, "id": "f", "title": "T", "submitLabel": "Save", "questions": [ { "id": "q", "type": "text", "question": "Q" } ] }""");

        Assert.NotNull(read.Form);
        Assert.Contains(read.Problems, problem => problem.Contains("Version 2"));
    }

    [Fact]
    public void PrefilledAnswers_AreReadByWhatTheQuestionTakes()
    {
        var read = DynamicFormReader.Read("""
            { "id": "f", "title": "T", "submitLabel": "Save", "questions": [
              { "id": "phone", "type": "phone", "question": "Q", "value": { "phone": "+2348035551234" } },
              { "id": "fee", "type": "money", "question": "Q", "value": { "number": 45000 } },
              { "id": "agree", "type": "consent", "question": "Q", "value": { "agreed": true } },
              { "id": "areas", "type": "tags", "question": "Q", "value": { "items": ["Lekki", "Ikeja"] } },
              { "id": "when", "type": "datetime", "question": "Q", "value": { "date": "2026-12-24", "time": "14:30" } }
            ] }
            """);

        var given = read.Form!.Prefilled;
        Assert.Equal("+2348035551234", given["phone"].Phone);
        Assert.Equal("08035551234", given["phone"].Text);
        Assert.Equal(45000m, given["fee"].Number);
        Assert.Equal("45,000", given["fee"].Text);
        Assert.True(given["agree"].Agreed);
        Assert.Equal(["Lekki", "Ikeja"], given["areas"].Items!);
        Assert.Equal(new DateOnly(2026, 12, 24), given["when"].Date);
        Assert.Equal(new TimeOnly(14, 30), given["when"].Time);
    }

    [Fact]
    public void OnlyTheFirstRecommendedOption_IsRecommended()
    {
        var read = DynamicFormReader.Read("""{ "id": "f", "title": "T", "submitLabel": "Save", "questions": [ { "id": "q", "type": "one", "question": "Q", "options": [ { "label": "A", "recommended": true }, { "label": "B", "recommended": true } ] } ] }""");

        Assert.Equal([true, false], read.Form!.Questions[0].Options!.Select(option => option.Recommended));
    }

    [Fact]
    public void TheBackendsResponse_CanCarryAFormAndAMessage_OrSayItsDone()
    {
        var next = ApiDynamicFormSource.Read($$"""{ "form": {{Payments}}, "message": "Saved." }""");
        Assert.Equal("payments", next.Form!.Id);
        Assert.Equal("Saved.", next.Message);

        var bare = ApiDynamicFormSource.Read(Payments);
        Assert.Equal("payments", bare.Form!.Id);

        var done = ApiDynamicFormSource.Read("""{ "form": null, "message": "Saved to your job description." }""");
        Assert.Null(done.Form);
        Assert.Equal("Saved to your job description.", done.Message);
    }
}
