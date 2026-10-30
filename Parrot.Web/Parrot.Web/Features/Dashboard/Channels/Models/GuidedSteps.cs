namespace Parrot.Web.Features.Dashboard.Channels.Models;

/// <param name="Label">Optional heading for the instruction — Meta names some of its screens.</param>
/// <param name="Text">The instruction. **double asterisks** mark the words that appear on Meta's screen.</param>
/// <param name="Link">
/// A way out to the provider, shown under this instruction. It sits on the
/// instruction rather than the step because "open the link below" is only true
/// if the link is directly below it.
/// </param>
public sealed record GuidedInstruction(string Text, string? Label = null, GuidedLink? Link = null);

public sealed record GuidedLink(string Label, string Url);

public sealed record GuidedStep(
    string Title,
    IReadOnlyList<GuidedInstruction> Instructions,
    string? Eyebrow = null,
    string? Intro = null,
    string? Aside = null);

/// <summary>
/// The manual path, written as the things Meta actually requires.
///
/// An earlier four-step version skipped number verification and pointed at the
/// temporary token on API Setup — which expires in about a day and takes the
/// connection down with it, silently. These seven are the steps that leave a
/// connection still working next week.
///
/// Steps 5 and 6 have no instructions of their own: one is the connection
/// workspace and the other is the shared message test.
/// </summary>
public static class GuidedSteps
{
    public static readonly IReadOnlyList<GuidedStep> All =
    [
        new("Add your app in Meta",
        [
            new("Open the link below and sign in with the Facebook account you prepared.",
                Link: new("Open Meta Business Settings", "https://business.facebook.com/settings/apps")),
            new("In the left menu, find **Accounts**. If it is closed, select it to reveal its options."),
            new("Select **Apps**, then choose **Add**.")
        ],
            Aside: "Keep yára open in another tab so you can follow each step."),

        new("Name your Meta app",
        [
            new("Enter an **app name** you can recognise, such as your business name followed by “WhatsApp”."),
            new("Add a **contact email** you check regularly and can access now."),
            new("After completing both fields, select **Next**.")
        ]),

        new("Add your use cases",
        [
            new("Leave **Featured** selected on the left, then tick **Create & manage ads with Marketing API**."),
            new("Tick **Create & manage app ads with Meta Ads Manager**."),
            new("Tick **Connect with customers through WhatsApp**, then select **Next**. This is the one that adds WhatsApp to your app.")
        ],
            Aside: "Without the WhatsApp use case, the WhatsApp menu never appears and the later steps have nothing to point at."),

        new("Complete Meta's Remaining Screens",
        [
            new("Select the Business Portfolio you want to use for WhatsApp.", "Business"),
            new("Review the information and complete anything Meta asks for.", "Requirements"),
            new("Check your selections, then choose **Create app**.", "Overview")
        ],
            Intro: "Meta will take you through the remaining screens. Complete each one, then create the app before returning to yára.",
            Aside: "Meta may adjust or skip a screen depending on your business account. Continue until you see your new app dashboard."),

        new("Open WhatsApp API Setup",
        [
            new("On the Dashboard, find **App customization and requirements**."),
            new("Select **Customize the Connect with customers through WhatsApp use case**."),
            new("On the Customize use case screen, select **API Setup** from the left menu.")
        ],
            Aside: "Meta may group or rename this area as Quickstart, Connect on WhatsApp or Production setup."),

        // The connection workspace renders here.
        new("Finish Your Connection", [], Eyebrow: "Final setup"),

        // The shared message test renders here, and brings its own heading.
        new("Send a Real Message to Test", [], Eyebrow: "Live check")
    ];

    /// <summary>
    /// The steps that carry a drawing of Meta's screen. Asking whether the
    /// Demo fragment is null cannot answer this: the parent always supplies
    /// one, and it simply renders nothing on the steps without a drawing — so
    /// every step looked like a drawing step and took the wide layout.
    /// </summary>
    public static bool HasDrawing(int step) => step is 0 or 1 or 2 or 4;

    public const int ConnectionStep = 5;
    public const int TestStep = 6;

    public static int Count => All.Count;
}
