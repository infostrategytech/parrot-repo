using System.Text.RegularExpressions;
using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Dashboard.Knowledge;

namespace Parrot.Web.Features.Settings;

/// <summary>A contact detail found elsewhere, and where, so the field can say so.</summary>
public sealed record ContactSuggestion(string Field, string Value, string Source);

/// <summary>
/// Contact details the business has already given yára (designer, 7 November 2026): its connected channels (the
/// WhatsApp number, the Instagram username) and what it told its AI in Knowledge (an email, a website, a number).
/// They only fill empty fields, each says where it came from, and nothing is kept until the business saves.
/// Until the profile API returns these itself (settings-backend-requests), they're read here from what the
/// frontend already has.
/// </summary>
public static partial class BusinessContactSuggestions
{
    public const string Instagram = nameof(BusinessContact.Instagram), TikTok = nameof(BusinessContact.TikTok),
        WhatsApp = nameof(BusinessContact.WhatsApp), Phone = nameof(BusinessContact.Phone), Email = nameof(BusinessContact.Email),
        Website = nameof(BusinessContact.Website), Facebook = nameof(BusinessContact.Facebook);

    public static IReadOnlyList<ContactSuggestion> Find(BusinessContact current, IEnumerable<BusinessChannel> channels, IEnumerable<KnowledgeFact> facts)
    {
        var found = new Dictionary<string, ContactSuggestion>();
        void Offer(string field, string? value, string source)
        {
            if (string.IsNullOrWhiteSpace(value) || found.ContainsKey(field) || !string.IsNullOrWhiteSpace(Read(current, field))) return;
            found[field] = new(field, value.Trim(), source);
        }

        // A connected account is the surest source, so channels go first.
        foreach (var channel in channels.Where(channel => channel.Status == ChannelStatus.Connected))
        {
            var account = channel.AccountName;
            switch (channel.Id)
            {
                case ChannelId.Whatsapp when account is not null && PhoneLike().IsMatch(account):
                    Offer(WhatsApp, account, "From your connected WhatsApp"); break;
                case ChannelId.Instagram when account?.StartsWith('@') == true:
                    Offer(Instagram, account[1..], "From your connected Instagram"); break;
                case ChannelId.TikTok when account?.StartsWith('@') == true:
                    Offer(TikTok, account[1..], "From your connected TikTok"); break;
            }
        }

        foreach (var fact in facts.Where(fact => fact.State == FactState.Known))
        {
            var source = fact.Source is { Length: > 0 } where ? $"From {where}, in Knowledge" : "From what you told your AI";
            var text = fact.Text;
            Offer(Email, EmailIn().Match(text) is { Success: true } email ? email.Value : null, source);
            Offer(Instagram, InstagramIn().Match(text) is { Success: true } ig ? ig.Groups[1].Value : null, source);
            Offer(TikTok, TikTokIn().Match(text) is { Success: true } tt ? tt.Groups[1].Value : null, source);
            Offer(Facebook, FacebookIn().Match(text) is { Success: true } fb ? fb.Groups[1].Value : null, source);
            // An email's domain isn't a website: look for one only in what's left.
            if (WebsiteIn().Match(EmailIn().Replace(text, " ")) is { Success: true } site && !IsSocial(site.Value)) Offer(Website, site.Value.TrimEnd('.', ','), source);
            if (PhoneLike().Match(text) is { Success: true } phone)
                Offer(text.Contains("whatsapp", StringComparison.OrdinalIgnoreCase) ? WhatsApp : Phone, phone.Value, source);
        }
        return [.. found.Values];
    }

    public static BusinessContact Apply(BusinessContact contact, IEnumerable<ContactSuggestion> suggestions) =>
        suggestions.Aggregate(contact, (current, item) => item.Field switch
        {
            Instagram => current with { Instagram = item.Value },
            TikTok => current with { TikTok = item.Value },
            WhatsApp => current with { WhatsApp = item.Value },
            Phone => current with { Phone = item.Value },
            Email => current with { Email = item.Value },
            Website => current with { Website = item.Value },
            Facebook => current with { Facebook = item.Value },
            _ => current,
        });

    private static string Read(BusinessContact contact, string field) => field switch
    {
        Instagram => contact.Instagram, TikTok => contact.TikTok, WhatsApp => contact.WhatsApp, Phone => contact.Phone,
        Email => contact.Email, Website => contact.Website, Facebook => contact.Facebook, _ => string.Empty,
    };

    private static bool IsSocial(string url) =>
        new[] { "instagram.com", "tiktok.com", "facebook.com", "fb.com", "wa.me", "whatsapp.com" }.Any(host => url.Contains(host, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")] private static partial Regex EmailIn();
    [GeneratedRegex(@"instagram\.com/([\w.]+)", RegexOptions.IgnoreCase)] private static partial Regex InstagramIn();
    [GeneratedRegex(@"tiktok\.com/@([\w.]+)", RegexOptions.IgnoreCase)] private static partial Regex TikTokIn();
    [GeneratedRegex(@"(?:facebook|fb)\.com/([\w.-]+)", RegexOptions.IgnoreCase)] private static partial Regex FacebookIn();
    [GeneratedRegex(@"\b(?:https?://)?(?:www\.)?[a-z0-9-]+\.(?:com|ng|com\.ng|shop|store|co|africa|net|org)(?:/\S*)?", RegexOptions.IgnoreCase)] private static partial Regex WebsiteIn();
    [GeneratedRegex(@"\+?\d[\d\s-]{8,}\d")] private static partial Regex PhoneLike();
}
