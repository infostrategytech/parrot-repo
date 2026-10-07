using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Dashboard.Channels.Models;

/// <param name="Title">What the expectation is, in a few words.</param>
/// <param name="Body">Why it matters, in the SME's terms.</param>
/// <param name="Details">Optional, behind a disclosure — the step-by-step nobody needs unless they are stuck.</param>
public sealed record RouteExpectation(
    string Title,
    string Body,
    IconName Glyph,
    string? Details = null,
    string? DetailsLabel = null);

/// <summary>The one thing on the page that is irreversible or easy to miss.</summary>
public sealed record RoutePriority(string Title, string Body, IconName? Glyph = null);

/// <summary>
/// A route a channel offers on its first screen.
///
/// Every channel uses the same shape, so the choice modal, the preparation page
/// and the failure screen are one implementation rather than one per channel.
/// </summary>
public sealed record ChannelRoute
{
    public required ChannelPath Id { get; init; }
    public required string Label { get; init; }
    public required string Explanation { get; init; }

    /// <summary>The short tag on the choice card — "Fresh Number", "Keep Your App".</summary>
    public required string Marker { get; init; }

    public required IconName Glyph { get; init; }
    public required string PageTitle { get; init; }
    public required string PageIntro { get; init; }
    public RoutePriority? Priority { get; init; }
    public IReadOnlyList<RouteExpectation> Expectations { get; init; } = [];

    /// <summary>The label on the button that opens Meta.</summary>
    public required string Cta { get; init; }

    /// <summary>The assurance beside it, because handing over a login deserves one.</summary>
    public required string Assurance { get; init; }

    /// <summary>What the person confirms before the button unlocks.</summary>
    public required string Acknowledgement { get; init; }

    /// <summary>Shown on the failure screen when this route is the one that broke.</summary>
    public IReadOnlyList<string> Recovery { get; init; } = [];
}

public sealed record PathChoiceCopy(string Title, string Intro);

public sealed record ProviderPopupCopy(string Provider, IconName Glyph, string Header, string Body, string Cta);

public sealed record MessageTestCopy(string Title, string From, string Fallback);

/// <summary>
/// Everything the Channels flows say, per channel and per route.
///
/// It is data rather than markup because the three channels differ only in
/// wording — one set of screens reads all of this. WhatsApp offers three routes
/// because a number can arrive three ways; Instagram and Facebook offer one
/// each, because there is nothing to choose between.
/// </summary>
public static class ChannelRoutes
{
    private static readonly ChannelRoute[] Whatsapp =
    [
        new()
        {
            Id = ChannelPath.New,
            Label = "Connect a New Number",
            Explanation = "Set up a fresh number for customer messages in OmniAI.",
            Marker = "Fresh Number",
            Glyph = IconName.RiAddCircleFill,
            PageTitle = "Connect your New Number",
            PageIntro = "Meta will create the WhatsApp connection and verify that the number belongs to you.",
            Priority = new(
                "This Number Will Be Managed in OmniAI",
                "After setup, new customer messages to this number will appear in OmniAI. Use a number that is not currently active on WhatsApp.",
                IconName.RiSmartphoneFill),
            Expectations =
            [
                new("Keep the Phone Nearby", "Meta will send a code by SMS or call.", IconName.RiSmartphoneFill),
                new("Use the Name Customers Know", "Enter the brand or business name your customers recognise. It does not have to be your registered company name.", IconName.RiStore2Fill)
            ],
            Cta = "Continue with Facebook",
            Assurance = "Meta handles your Facebook login. OmniAI never sees your password.",
            Acknowledgement = "This number is not currently on WhatsApp, and I can receive a verification code on it.",
            Recovery =
            [
                "Confirm the number is not currently on WhatsApp.",
                "Make sure it can receive an SMS or voice call."
            ]
        },
        new()
        {
            Id = ChannelPath.Coexistence,
            Label = "Use My WhatsApp Business Number",
            Explanation = "Connect the number customers already know and keep using the Business app.",
            Marker = "Keep Your App",
            Glyph = IconName.RiSmartphoneFill,
            PageTitle = "Use your Existing Business Number",
            PageIntro = "Your WhatsApp Business app stays active while OmniAI helps manage the same number.",
            Priority = new(
                "Your Chat History Choice Happens Once",
                "Meta may ask whether to share up to 180 days of eligible chats. Choose Share if you want them in OmniAI, you may not get another opportunity."),
            Expectations =
            [
                new("Your App Stays Available", "You can continue one-to-one conversations in WhatsApp Business.", IconName.RiChat3Fill),
                new("Group Chats and Calls Stay in WhatsApp", "They will not appear in OmniAI, and your AI agent cannot respond to them.", IconName.RiGroupFill),
                new("Some Devices May Need Reconnecting", "Linked devices may sign out during setup. You can reconnect supported devices afterwards.", IconName.RiComputerFill,
                    "WhatsApp for Windows and Wear OS are not supported with this connection.", "View Device Limitations"),
                new("Keep the App Active", "Open WhatsApp Business at least once every two weeks to stay connected.", IconName.RiCalendarCheckFill)
            ],
            Cta = "Continue with Facebook",
            Assurance = "Meta handles your Facebook login. OmniAI never sees your password.",
            Acknowledgement = "I understand the one-time chat-history choice and that group chats, calls and some devices will not sync with OmniAI.",
            Recovery =
            [
                "Update the WhatsApp Business app to the latest version.",
                "Open the app on the main phone before trying again."
            ]
        },
        new()
        {
            Id = ChannelPath.Migration,
            Label = "Move from Another Provider",
            Explanation = "Bring an existing WhatsApp API number from your current platform.",
            Marker = "Switch Provider",
            Glyph = IconName.RiExchangeBoxFill,
            PageTitle = "Move your Number to OmniAI",
            PageIntro = "Meta will transfer your WhatsApp Business Platform number from your current provider.",
            Priority = new(
                "Your Number Moves, Provider Data Does Not",
                "Your WhatsApp number can move to OmniAI. Chats, contacts, tags, notes and automations from your current provider will not.",
                IconName.RiExchangeBoxFill),
            Expectations =
            [
                new("Complete Meta's Checks First", "Your business and WhatsApp account must be approved, with a valid payment method.", IconName.RiShieldCheckFill),
                new("Switch Off Two-Step Verification", "Do this in WhatsApp Manager, or ask your current provider to do it.", IconName.RiLock2Fill),
                new("Keep the Number Available", "Meta may send a verification code by SMS or phone call.", IconName.RiSmartphoneFill),
                new("Test Before Cancelling", "Keep your current provider until messages work in OmniAI.", IconName.RiCheckboxCircleFill)
            ],
            Cta = "Continue with Facebook",
            Assurance = "Meta handles your Facebook login. OmniAI never sees your password.",
            Acknowledgement = "I have completed Meta's requirements, saved the provider data I need and will keep my provider active until messages work in OmniAI.",
            Recovery =
            [
                "Switch off two-step verification for the number.",
                "Confirm business verification and add a payment method in Meta."
            ]
        }
    ];

    // Instagram Login needs no Facebook Page and still covers messaging,
    // comments, publishing and insights, so a second route would only ask the
    // SME to make a choice with no real consequence.
    private static readonly ChannelRoute[] Instagram =
    [
        new()
        {
            Id = ChannelPath.InstagramLogin,
            Label = "Connect with Instagram",
            Explanation = "Sign in with Instagram. No Facebook Page needed.",
            Marker = "Simplest",
            Glyph = IconName.RiInstagramFill,
            PageTitle = "Connect with your Instagram Login",
            PageIntro = "Instagram opens a window where you approve OmniAI. You never leave your Instagram account.",
            Priority = new(
                "You Do Not Need a Facebook Page",
                "OmniAI signs in with Instagram directly, so it makes no difference whether you have a Facebook Page or whether it is linked.",
                IconName.RiInstagramFill),
            Expectations =
            [
                new("You Need a Professional Account", "Your Instagram must be a Business or Creator account. Switching is free and takes a minute in Instagram settings.", IconName.RiStore2Fill,
                    "Instagram → Settings → Account type and tools → Switch to professional account."),
                new("Approve the Full List", "Instagram asks once, for your messages, comments and replies together. Approve all of it so your inbox is complete.", IconName.RiChat3Fill),
                new("Story Replies and Mentions Arrive Too", "Replies to your stories and @mentions come into the same inbox as DMs.", IconName.RiGroupFill),
                new("Messages Stay in Instagram as Well", "You can keep using the Instagram app. OmniAI does not take the account over.", IconName.RiSmartphoneFill)
            ],
            Cta = "Continue with Instagram",
            Assurance = "Instagram handles the sign-in. OmniAI never sees your password.",
            Acknowledgement = "My Instagram is a Business or Creator account, and I am ready to approve the full list.",
            Recovery =
            [
                "Check your Instagram is a Business or Creator account, not personal.",
                "Make sure you are signed in as the account owner, not a collaborator."
            ]
        }
    ];

    // Signing in and picking a Page is the only way in, so there is nothing to
    // choose between. Unlike WhatsApp this needs no Tech Provider approval.
    private static readonly ChannelRoute[] Facebook =
    [
        new()
        {
            Id = ChannelPath.FacebookPage,
            Label = "Connect your Facebook Page",
            Explanation = "Sign in with Facebook and pick the Page customers message.",
            Marker = "Your Page",
            Glyph = IconName.RiFacebookCircleFill,
            PageTitle = "Connect your Facebook Page",
            PageIntro = "Facebook opens a window where you choose the Page whose messages should reach OmniAI.",
            Priority = new(
                "Messages Come from a Page, Not a Profile",
                "OmniAI connects to a Facebook Page. A personal profile cannot receive business messages, so you need a Page before you start.",
                IconName.RiFacebookCircleFill),
            Expectations =
            [
                new("You Need a Page You Manage", "Create one free in Facebook if you do not have one yet. It takes a few minutes.", IconName.RiStore2Fill,
                    "Facebook → Menu → Pages → Create new Page."),
                new("Your Role Must Include Messaging", "Not every Page role can reply to messages. If your Page list looks empty, ask the owner to give you messaging access.", IconName.RiShieldCheckFill),
                new("Approve the Full List", "Facebook asks once, for your messages and your Page details together. Approve all of it so your inbox is complete.", IconName.RiChat3Fill),
                new("You Keep Replying in Facebook", "Facebook and Business Suite carry on working. OmniAI does not take the Page over.", IconName.RiSmartphoneFill)
            ],
            Cta = "Continue with Facebook",
            Assurance = "Facebook handles the sign-in. OmniAI never sees your password.",
            Acknowledgement = "I manage a Facebook Page and my role lets me reply to its messages.",
            Recovery =
            [
                "Check you are signed in to an account that manages the Page.",
                "Ask the Page owner for a role that includes messaging."
            ]
        }
    ];

    // X: one route, the same shape as Facebook's — sign in, approve DMs.
    private static readonly ChannelRoute[] X =
    [
        new()
        {
            Id = ChannelPath.XLogin,
            Label = "Connect your X account",
            Explanation = "Sign in with X and approve access to your DMs.",
            Marker = "Your Account",
            Glyph = IconName.RiChat3Fill,
            PageTitle = "Connect your X account",
            PageIntro = "X opens a window where you approve OmniAI. You stay signed in to X as usual.",
            Priority = new(
                "Customers Must Be Able to DM You",
                "OmniAI can only bring in the DMs X lets through. If only people you follow can message you, open your DMs to everyone before you start.",
                IconName.RiChat3Fill),
            Expectations =
            [
                new("Use Your Business's Account", "Sign in with the X account your customers know, not a personal one.", IconName.RiUser3Fill),
                new("Open Your DMs to Everyone", "Allow message requests from everyone, so customers you don't follow can reach you.", IconName.RiLock2Fill,
                    "X → Settings → Privacy and safety → Direct messages → Allow message requests from everyone."),
                new("Approve Read and Write", "X asks once to read and send your DMs. Approve both so OmniAI can reply.", IconName.RiShieldCheckFill),
                new("You Keep Replying in X", "The X app carries on working. OmniAI does not take the account over.", IconName.RiSmartphoneFill)
            ],
            Cta = "Continue with X",
            Assurance = "X handles the sign-in. OmniAI never sees your password.",
            Acknowledgement = "This is my business's X account and anyone can send it a DM.",
            Recovery =
            [
                "Check you are signed in to your business's X account.",
                "Allow message requests from everyone in X's DM settings."
            ]
        }
    ];

    public static IReadOnlyList<ChannelRoute> For(ChannelId channel) => channel switch
    {
        ChannelId.Whatsapp => Whatsapp,
        ChannelId.Instagram => Instagram,
        ChannelId.Facebook => Facebook,
        ChannelId.X => X,
        _ => []
    };

    public static ChannelRoute? Find(ChannelId channel, ChannelPath? path) =>
        path is null ? null : For(channel).FirstOrDefault(route => route.Id == path);

    public static PathChoiceCopy Choice(ChannelId channel) => channel switch
    {
        ChannelId.Whatsapp => new("How Do You Want to Connect WhatsApp?",
            "Choose what describes your number today. We’ll guide you through the rest."),
        ChannelId.Instagram => new("How Do You Want to Connect Instagram?",
            "Both routes bring your DMs into OmniAI. Choose the one that matches your account."),
        ChannelId.X => new("How Do You Want to Connect X?",
            "Choose the option that matches your account."),
        _ => new("How Do You Want to Connect Facebook?",
            "Choose the option that matches your Page.")
    };

    /// <summary>
    /// Whose window opens, and what it says. WhatsApp and Facebook hand off to
    /// Meta; Instagram Login hands off to Instagram itself, so calling it
    /// "Meta" there would name a company the SME never sees on screen.
    /// </summary>
    public static ProviderPopupCopy Popup(ChannelId channel) => channel switch
    {
        ChannelId.Whatsapp => new("Meta", IconName.RiFacebookCircleFill,
            "Facebook · Connect your WhatsApp Business account",
            "This is Meta’s window. Your business signs in, picks a business portfolio and WhatsApp account, verifies the number by SMS, and approves access.",
            "Finish in Meta"),
        ChannelId.Instagram => new("Instagram", IconName.RiInstagramFill,
            "Instagram · Connect your professional account",
            "This is Instagram’s window. You sign in, choose your professional account, and approve the access OmniAI asked for.",
            "Finish in Instagram"),
        // The sprite has no X glyph; the popup draws ChannelIcon's X mark instead of this one.
        ChannelId.X => new("X", IconName.RiExternalLinkLine,
            "X · Authorize OmniAI",
            "This is X’s window. You sign in, and approve read and write access to your direct messages for OmniAI.",
            "Finish in X"),
        _ => new("Facebook", IconName.RiFacebookCircleFill,
            "Facebook · Connect your Page",
            "This is Facebook’s window. You sign in, choose the Page you manage, and approve the access OmniAI asked for.",
            "Finish in Facebook")
    };

    /// <summary>The live check reads differently per channel: a number, a handle, a Page.</summary>
    public static MessageTestCopy Test(ChannelId channel) => channel switch
    {
        ChannelId.Whatsapp => new("Send a Message to your Number", "From another phone,", "your business number"),
        ChannelId.Instagram => new("Send a DM to your Account", "From another Instagram account,", "your Instagram account"),
        ChannelId.X => new("Send a DM to your Account", "From another X account,", "your X account"),
        _ => new("Send a Message to your Page", "From another Facebook account,", "your Facebook Page")
    };

    /// <summary>The three real calls after Meta hands back the code, named per channel.</summary>
    public static IReadOnlyList<string> ConnectingSteps(ChannelId channel) => channel switch
    {
        ChannelId.Whatsapp => ["Confirming your WhatsApp account", "Preparing your business number", "Turning on your messages"],
        ChannelId.Instagram => ["Confirming your Instagram account", "Linking your professional profile", "Turning on your messages"],
        ChannelId.X => ["Confirming your X account", "Checking your permissions", "Turning on your messages"],
        _ => ["Confirming your Facebook account", "Linking your Page", "Turning on your messages"]
    };

    /// <summary>The three stages those calls move through, in order.</summary>
    public static readonly EmbeddedStage[] ConnectingStages =
        [EmbeddedStage.Linking, EmbeddedStage.Registering, EmbeddedStage.Subscribing];
}
