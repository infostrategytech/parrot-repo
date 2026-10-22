using System.Globalization;
using System.Net;
using System.Text;

namespace Parrot.Application.Teams;

/// <summary>What an invite email says, before it is rendered.</summary>
/// <param name="Access">One line per grant: where, as what, on which channels.</param>
public sealed record InviteEmailModel(
    string TeamName,
    string? TeamLogoUrl,
    string InviterName,
    string InviteeName,
    string InviteeEmail,
    IReadOnlyList<InviteEmailAccess> Access,
    string AcceptUrl,
    DateTime ExpiresAt,
    string BrandLogoUrl);

/// <summary>One grant as the email lists it, e.g. Catering · Conversation Manager · WhatsApp.</summary>
public sealed record InviteEmailAccess(string Workspace, string Role, string Channels);

/// <summary>
/// The invite email, as HTML and as plain text. The HTML is built the way mail
/// clients render reliably (Gmail, Outlook and Apple Mail alike): tables for
/// layout, every style inline, no web fonts or scripts, and the button drawn as
/// a filled table cell so it shows even where background images and padding on
/// links do not. Every value a person typed is HTML-encoded. The plain-text part
/// travels with it, which reads better in text-only clients and counts in the
/// message's favour with spam filters.
/// </summary>
public static class InviteEmail
{
    // The design system's brand and neutral tokens, as literal colours: mail
    // clients do not read CSS variables. primary-500, primary-600, primary-50,
    // zinc-900, zinc-500, zinc-200 and zinc-50.
    private const string Brand = "#587b7e";
    private const string BrandDark = "#496669";
    private const string BrandTint = "#f2f7f8";
    private const string Ink = "#18181b";
    private const string Muted = "#71717b";
    private const string Hairline = "#e4e4e7";
    private const string Page = "#fafafa";
    private const string Fonts = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    public static string Subject(InviteEmailModel model) => $"{model.InviterName} invited you to {model.TeamName} on OmniAI";

    public static string Html(InviteEmailModel model)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        string expires = model.ExpiresAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string firstName = model.InviteeName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? model.InviteeName;

        // The team's own logo when it has one; otherwise its initials on the brand tint.
        string teamMark = model.TeamLogoUrl is { Length: > 0 } logo
            ? $"""<img src="{E(logo)}" width="48" height="48" alt="" style="display:block;width:48px;height:48px;border-radius:12px;object-fit:cover;border:0">"""
            : $"""<div style="width:48px;height:48px;border-radius:12px;background:{BrandTint};color:{BrandDark};font:600 18px/48px {Fonts};text-align:center">{E(Initials(model.TeamName))}</div>""";

        StringBuilder access = new();
        foreach (InviteEmailAccess line in model.Access)
        {
            access.Append($"""
                <tr>
                  <td style="padding:12px 16px;border-top:1px solid {Hairline}">
                    <div style="font:600 15px/1.4 {Fonts};color:{Ink}">{E(line.Workspace)}</div>
                    <div style="font:400 13px/1.5 {Fonts};color:{Muted}">{E(line.Role)} &middot; {E(line.Channels)}</div>
                  </td>
                </tr>
                """);
        }

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="supported-color-schemes" content="light">
            <title>{E(Subject(model))}</title>
            </head>
            <body style="margin:0;padding:0;background:{Page}">
            <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:{Page}">{E(model.InviterName)} invited you to work in {E(model.TeamName)}. The invite works until {expires}.</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Page}">
              <tr>
                <td align="center" style="padding:32px 16px">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px">
                    <tr>
                      <td style="padding:0 4px 20px">
                        <img src="{E(model.BrandLogoUrl)}" width="112" alt="OmniAI" style="display:block;width:112px;height:auto;border:0">
                      </td>
                    </tr>
                    <tr>
                      <td style="background:#ffffff;border:1px solid {Hairline};border-radius:16px;padding:32px">
                        <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                          <tr>
                            <td style="padding-right:14px;vertical-align:middle">{teamMark}</td>
                            <td style="vertical-align:middle;font:500 13px/1.4 {Fonts};color:{Muted}">{E(model.TeamName)}</td>
                          </tr>
                        </table>
                        <h1 style="margin:24px 0 8px;font:700 24px/1.3 {Fonts};color:{Ink}">Join {E(model.TeamName)} on OmniAI</h1>
                        <p style="margin:0 0 24px;font:400 15px/1.6 {Fonts};color:{Ink}">Hi {E(firstName)}, {E(model.InviterName)} has invited you to help run their conversations on OmniAI. Here is what you will have access to:</p>
                        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Hairline};border-radius:12px;border-collapse:separate;overflow:hidden">
                          <tr><td style="padding:10px 16px;background:{Page};font:600 11px/1.4 {Fonts};letter-spacing:.08em;text-transform:uppercase;color:{Muted}">Your access</td></tr>
                          {access}
                        </table>
                        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:28px 0 8px">
                          <tr>
                            <td align="center" bgcolor="{Brand}" style="border-radius:10px;background:{Brand}">
                              <a href="{E(model.AcceptUrl)}" style="display:inline-block;padding:14px 28px;font:600 15px/1 {Fonts};color:#ffffff;text-decoration:none;border-radius:10px">Accept invite</a>
                            </td>
                          </tr>
                        </table>
                        <p style="margin:16px 0 0;font:400 13px/1.6 {Fonts};color:{Muted}">Accepting signs you in with <strong style="color:{Ink}">{E(model.InviteeEmail)}</strong>, or sets up your OmniAI login if you don't have one yet. One login works for every team you join.</p>
                        <p style="margin:12px 0 0;font:400 13px/1.6 {Fonts};color:{Muted}">This invite works until <strong style="color:{Ink}">{expires}</strong>.</p>
                        <p style="margin:24px 0 0;padding-top:16px;border-top:1px solid {Hairline};font:400 12px/1.6 {Fonts};color:{Muted}">If the button doesn't work, copy this link into your browser:<br><a href="{E(model.AcceptUrl)}" style="color:{BrandDark};word-break:break-all">{E(model.AcceptUrl)}</a></p>
                      </td>
                    </tr>
                    <tr>
                      <td style="padding:20px 4px 0;font:400 12px/1.6 {Fonts};color:{Muted}">You received this because {E(model.InviterName)} invited {E(model.InviteeEmail)} to their team. If you weren't expecting it, you can ignore this email; nothing happens unless you accept.</td>
                    </tr>
                  </table>
                </td>
              </tr>
            </table>
            </body>
            </html>
            """;
    }

    public static string Text(InviteEmailModel model)
    {
        string firstName = model.InviteeName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? model.InviteeName;
        StringBuilder text = new();
        text.AppendLine($"Join {model.TeamName} on OmniAI");
        text.AppendLine();
        text.AppendLine($"Hi {firstName}, {model.InviterName} has invited you to help run their conversations on OmniAI.");
        text.AppendLine();
        text.AppendLine("Your access:");
        foreach (InviteEmailAccess line in model.Access)
        {
            text.AppendLine($"- {line.Workspace}: {line.Role}, {line.Channels}");
        }

        text.AppendLine();
        text.AppendLine("Accept the invite:");
        text.AppendLine(model.AcceptUrl);
        text.AppendLine();
        text.AppendLine($"Accepting signs you in with {model.InviteeEmail}, or sets up your OmniAI login if you don't have one yet.");
        text.AppendLine($"This invite works until {model.ExpiresAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.");
        text.AppendLine();
        text.AppendLine("If you weren't expecting this, you can ignore it; nothing happens unless you accept.");
        return text.ToString();
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}
