using System.Text.Json.Serialization;

namespace Parrot.Domain.Enums;

// The names are what a message's "source" reads as on Kafka; the numbers are
// what MongoDB stores, so neither may change.
public enum MessagingPlatform
{
    [JsonStringEnumMemberName("whatsapp")] WhatsApp = 1,
    [JsonStringEnumMemberName("facebook")] Facebook = 2,
    [JsonStringEnumMemberName("tiktok")] TikTok = 3,
    [JsonStringEnumMemberName("instagram")] Instagram = 4,
}
