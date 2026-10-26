using System.Text.Json.Nodes;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Features.DynamicForms;

namespace Test.Parrot.Api.Tests.Web.DynamicForms;

// The answers go back as a FormResult (dynamic-form-schema.json): by question id, in the shape each kind answers with.
public class DynamicFormWriterTests
{
    private static DynamicForm Form(params FormQuestion[] questions) =>
        new("form-1", "T", null, null, questions, new Dictionary<string, FormAnswer>(), "Save", true, true, null, null);

    private static JsonObject Answers(DynamicForm form, Dictionary<string, FormAnswer> answers, DynamicFormEnd ended = DynamicFormEnd.Submitted)
    {
        var result = JsonNode.Parse(DynamicFormWriter.Write(form, answers, ended))!.AsObject();
        return result["answers"]!.AsObject();
    }

    [Fact]
    public void TheResult_SaysWhichFormAndHowItEnded()
    {
        var form = Form(new FormQuestion("q", "Q", FormQuestionKind.Text));
        foreach (var (ended, word) in new[] { (DynamicFormEnd.Submitted, "submitted"), (DynamicFormEnd.Decided, "decided"), (DynamicFormEnd.Skipped, "skipped") })
        {
            var result = JsonNode.Parse(DynamicFormWriter.Write(form, new Dictionary<string, FormAnswer>(), ended))!;
            Assert.Equal("form-1", (string?)result["formId"]);
            Assert.Equal(word, (string?)result["ended"]);
        }
    }

    [Fact]
    public void Choices_ComeBackPicked_InTheOrderShown_WithTheirOwnWords()
    {
        var form = Form(new FormQuestion("pay", "Q", FormQuestionKind.Many, [new("Card"), new("Transfer")], AllowOther: true));

        var value = Answers(form, new() { ["pay"] = new FormAnswer(["Card", "Transfer"], "Crypto") })["pay"]!;

        Assert.Equal("""{"picked":["Card","Transfer"],"text":"Crypto"}""", value.ToJsonString(DynamicFormWriter.Json));
    }

    [Fact]
    public void EachKind_AnswersWithItsOwnField()
    {
        var form = Form(
            new FormQuestion("phone", "Q", FormQuestionKind.Phone),
            new FormQuestion("fee", "Q", FormQuestionKind.Money),
            new FormQuestion("people", "Q", FormQuestionKind.Number),
            new FormQuestion("agree", "Q", FormQuestionKind.Consent),
            new FormQuestion("areas", "Q", FormQuestionKind.Tags),
            new FormQuestion("shots", "Q", FormQuestionKind.Photos),
            new FormQuestion("day", "Q", FormQuestionKind.Date),
            new FormQuestion("when", "Q", FormQuestionKind.DateTime),
            new FormQuestion("email", "Q", FormQuestionKind.Email),
            new FormQuestion("hours", "Q", FormQuestionKind.Text) { RequestedType = "hours" });

        var answers = Answers(form, new()
        {
            ["phone"] = FormAnswer.None with { Text = "0803 555 1234", Phone = "+2348035551234" },
            ["fee"] = FormAnswer.None with { Text = "45,000", Number = 45000m },
            ["people"] = FormAnswer.None with { Text = "12", Number = 12m },
            ["agree"] = FormAnswer.None with { Agreed = true },
            ["areas"] = FormAnswer.None with { Items = ["Lekki", "Ikeja"] },
            ["shots"] = FormAnswer.None with { Files = ["file-1"] },
            ["day"] = FormAnswer.None with { Date = new DateOnly(2026, 12, 24) },
            ["when"] = FormAnswer.None with { Date = new DateOnly(2026, 12, 24), Time = new TimeOnly(9, 5) },
            ["email"] = FormAnswer.None with { Text = " ada@kitchen.ng " },
            ["hours"] = FormAnswer.None with { Text = "Mon to Fri, 9 to 5" },
        });

        Assert.Equal("""{"phone":"+2348035551234"}""", answers["phone"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"number":45000}""", answers["fee"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"number":12}""", answers["people"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"agreed":true}""", answers["agree"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"items":["Lekki","Ikeja"]}""", answers["areas"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"files":["file-1"]}""", answers["shots"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"date":"2026-12-24"}""", answers["day"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"date":"2026-12-24","time":"09:05"}""", answers["when"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"text":"ada@kitchen.ng"}""", answers["email"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"text":"Mon to Fri, 9 to 5"}""", answers["hours"]!.ToJsonString(DynamicFormWriter.Json));
    }

    [Fact]
    public void AnEmptyAnswer_IsAnEmptyValue_AndANote_IsLeftOut()
    {
        var form = Form(new FormQuestion("q", "Q", FormQuestionKind.Short), new FormQuestion("n", "These are the hours customers will see.", FormQuestionKind.Note));

        var answers = Answers(form, new Dictionary<string, FormAnswer>());

        Assert.Equal("{}", answers["q"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.False(answers.ContainsKey("n"));
    }

    [Fact]
    public void Unchecked_WhenDecided_AnAnswerThatDoesntReadAsItsKind_GoesBackAsTheirWords()
    {
        var form = Form(new FormQuestion("phone", "Q", FormQuestionKind.Phone), new FormQuestion("fee", "Q", FormQuestionKind.Money));

        var answers = Answers(form, new()
        {
            ["phone"] = FormAnswer.None with { Text = "call me" },
            ["fee"] = FormAnswer.None with { Text = "about half" },
        }, DynamicFormEnd.Decided);

        Assert.Equal("""{"text":"call me"}""", answers["phone"]!.ToJsonString(DynamicFormWriter.Json));
        Assert.Equal("""{"text":"about half"}""", answers["fee"]!.ToJsonString(DynamicFormWriter.Json));
    }
}
