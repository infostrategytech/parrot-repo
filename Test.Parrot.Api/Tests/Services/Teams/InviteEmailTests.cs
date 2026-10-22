using Parrot.Application.Teams;

namespace Test.Parrot.Api.Tests.Services.Teams;

public class InviteEmailTests
{
    private static InviteEmailModel Model(string teamName = "Oma's Bakery", string? logo = null) => new(
        teamName,
        logo,
        "Oma Ade",
        "Ada Obi",
        "ada@example.com",
        [
            new InviteEmailAccess("Catering", "Conversation Manager", "WhatsApp"),
            new InviteEmailAccess("All workspaces", "Analyst", "All channels"),
        ],
        "https://app.example.com/invite/eyJ.token",
        new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
        "https://app.example.com/brand/omniai-logo-512.png");

    [Fact]
    public void TheHtml_ListsEachGrant_TheButton_AndTheExpiry()
    {
        string html = InviteEmail.Html(Model());

        Assert.Contains("Catering", html);
        Assert.Contains("Conversation Manager &middot; WhatsApp", html);
        Assert.Contains("Analyst &middot; All channels", html);
        Assert.Contains("href=\"https://app.example.com/invite/eyJ.token\"", html);
        Assert.Contains("28 October 2026", html);
        Assert.Contains("Hi Ada,", html);
    }

    // A team name is something a person typed: it is text in the email, never markup.
    [Fact]
    public void WhatPeopleTyped_IsEncoded()
    {
        string html = InviteEmail.Html(Model(teamName: "<script>alert(1)</script>"));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void WithoutATeamLogo_TheTeamsInitialsShow()
    {
        Assert.Contains(">OB<", InviteEmail.Html(Model()));
        Assert.Contains("src=\"https://cdn.example.com/logo.png\"", InviteEmail.Html(Model(logo: "https://cdn.example.com/logo.png")));
    }

    [Fact]
    public void ThePlainText_CarriesTheSameInvite()
    {
        string text = InviteEmail.Text(Model());

        Assert.Contains("- Catering: Conversation Manager, WhatsApp", text);
        Assert.Contains("https://app.example.com/invite/eyJ.token", text);
        Assert.Contains("28 October 2026", text);
        Assert.DoesNotContain("<", text);
    }
}
