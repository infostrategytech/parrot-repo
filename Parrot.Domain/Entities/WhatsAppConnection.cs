using System.Security.Cryptography;
using Parrot.Domain.Constants;

namespace Parrot.Domain.Entities;

public class WhatsAppConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string OwnerUserId { get; set; }

    public required string Name { get; set; }

    public string WebhookIdentifier { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Callers must encrypt the token before assigning it; never store plaintext here.
    public required string VerifyTokenEncrypted { get; set; }

    public string? WhatsAppBusinessAccountId { get; set; }

    public string? PhoneNumberId { get; set; }

    public string? DisplayPhoneNumber { get; set; }

    public DateTime? WebhookVerifiedAt { get; set; }

    // Set only after a valid received message has been persisted in MongoDB.
    public DateTime? FirstMessageReceivedAt { get; set; }

    public DateTime? DisabledAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Receiving configuration does not establish authorization to send messages.
    public string SetupStatus => DisabledAt.HasValue
        ? WhatsAppConnectionStatus.Disabled
        : !WebhookVerifiedAt.HasValue
            ? WhatsAppConnectionStatus.AwaitingWebhookVerification
            : !FirstMessageReceivedAt.HasValue
                ? WhatsAppConnectionStatus.AwaitingTestMessage
                : WhatsAppConnectionStatus.ReceivingConfigured;
}
