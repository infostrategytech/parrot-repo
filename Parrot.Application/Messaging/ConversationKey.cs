using Parrot.Domain.Enums;

namespace Parrot.Application.Messaging;

/// <summary>
/// Names a conversation by its two participants, whichever way a message
/// goes: "{platform}:{min}:{max}", with the two normalized ids sorted
/// ordinally. A to B and B to A give the same key, so both directions share
/// one Kafka partition and one history.
///
/// The platform prefix keeps a WhatsApp number and a Facebook id that happen
/// to be the same digits from ever naming the same conversation.
/// </summary>
public static class ConversationKey
{
    public static string Create(MessagingPlatform platform, string participantA, string participantB)
    {
        string a = NormalizeParticipant(platform, participantA);
        string b = NormalizeParticipant(platform, participantB);

        (string first, string second) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        return $"{Prefix(platform)}:{first}:{second}";
    }

    /// <summary>
    /// A WhatsApp participant written as a phone number in international form
    /// keeps only its digits, so "+234 801 234 5678" and "2348012345678" are
    /// one participant. Anything else, such as the business-scoped user id
    /// Meta sends for a customer who hides their number behind a username
    /// ("NG.1A2B…"), is an opaque id and is kept exactly, as Facebook and TikTok ids are.
    /// </summary>
    public static string NormalizeParticipant(MessagingPlatform platform, string participant)
    {
        string trimmed = (participant ?? string.Empty).Trim();
        string normalized = platform switch
        {
            MessagingPlatform.WhatsApp when IsPhoneNumber(trimmed) => new string(trimmed.Where(char.IsAsciiDigit).ToArray()),
            MessagingPlatform.WhatsApp or MessagingPlatform.Facebook or MessagingPlatform.TikTok or MessagingPlatform.Instagram => trimmed,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "No conversation key rule for this platform."),
        };

        return normalized.Length > 0
            ? normalized
            : throw new ArgumentException("A conversation participant cannot be empty.", nameof(participant));
    }

    // Digits with the punctuation a number is displayed with, and at least one digit.
    private static bool IsPhoneNumber(string value) =>
        value.Any(char.IsAsciiDigit)
        && value.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')');

    /// <summary>The platform a key was made for, from its prefix, or null for something that is not a key.</summary>
    public static MessagingPlatform? PlatformOf(string key)
    {
        string prefix = key.Split(':', 2)[0];
        foreach (MessagingPlatform platform in Enum.GetValues<MessagingPlatform>())
        {
            if (Prefix(platform) == prefix)
            {
                return platform;
            }
        }

        return null;
    }

    private static string Prefix(MessagingPlatform platform) => platform switch
    {
        MessagingPlatform.WhatsApp => "whatsapp",
        MessagingPlatform.Facebook => "facebook",
        MessagingPlatform.TikTok => "tiktok",
        MessagingPlatform.Instagram => "instagram",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "No conversation key rule for this platform."),
    };
}
