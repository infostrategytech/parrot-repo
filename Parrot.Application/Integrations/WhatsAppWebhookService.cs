using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public class WhatsAppWebhookService : IWhatsAppWebhookService
{
    private readonly WhatsAppSettings _settings;
    private readonly IAppLogger<WhatsAppWebhookService> _logger;
    private readonly IMessagePublisher _publisher;
    private readonly IWhatsAppConnectionRepository _connections;

    public WhatsAppWebhookService(
        IOptions<WhatsAppSettings> settings,
        IAppLogger<WhatsAppWebhookService> logger,
        IMessagePublisher publisher,
        IWhatsAppConnectionRepository connections)
    {
        _settings = settings.Value;
        _logger = logger;
        _publisher = publisher;
        _connections = connections;
    }

    public string GetWebhookUrl() =>
        $"{_settings.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/whatsapp";

    public bool TryVerifyChallenge(string hubMode, string hubVerifyToken, string hubChallenge, out string echoChallenge)
    {
        echoChallenge = string.Empty;

        if (hubMode != "subscribe" || hubVerifyToken != _settings.VerifyToken)
        {
            _logger.LogWarning("WhatsApp webhook challenge rejected. Mode={Mode} TokenMatch={Match}", hubMode, hubVerifyToken == _settings.VerifyToken);
            return false;
        }

        echoChallenge = hubChallenge;
        return true;
    }

    public bool IsValidSignature(string rawBody, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppSecret)
            || !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string receivedHash = signatureHeader["sha256=".Length..];
        if (receivedHash.Length != 64)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_settings.AppSecret),
            Encoding.UTF8.GetBytes(rawBody));

        try
        {
            byte[] received = Convert.FromHexString(receivedHash);
            return CryptographicOperations.FixedTimeEquals(received, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public Task ProcessEventAsync(WhatsAppWebhookPayload payload, CancellationToken cancellationToken = default) =>
        ProcessEventAsync(payload, null, null, cancellationToken);

    public async Task<bool> ProcessConnectionEventAsync(
        string identifier,
        WhatsAppWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        WhatsAppConnection? connection = await _connections.GetActiveByIdentifierAsync(identifier);
        if (connection?.WebhookVerifiedAt is null
            || string.IsNullOrEmpty(connection.WhatsAppBusinessAccountId)
            || string.IsNullOrEmpty(connection.PhoneNumberId)
            || payload.Entry.Count == 0
            || payload.Entry.Any(entry => entry.Id != connection.WhatsAppBusinessAccountId
                || entry.Changes.Any(change => change.Field == "messages"
                    && change.Value.Metadata.PhoneNumberId != connection.PhoneNumberId)))
        {
            return false;
        }

        await ProcessEventAsync(payload, connection.Id, connection.OwnerUserId, cancellationToken);
        return true;
    }

    private async Task ProcessEventAsync(
        WhatsAppWebhookPayload payload,
        Guid? connectionId,
        string? ownerUserId,
        CancellationToken cancellationToken)
    {
        foreach (WhatsAppEntry entry in payload.Entry)
        {
            foreach (WhatsAppChange change in entry.Changes)
            {
                if (change.Field != "messages")
                {
                    continue;
                }

                if (change.Value.Messages is { Count: > 0 } messages)
                {
                    Dictionary<string, string> contactNames = change.Value.Contacts?
                        .ToDictionary(c => c.WaId, c => c.Profile.Name)
                        ?? [];

                    List<IncomingMessage> batch = new List<IncomingMessage>(messages.Count);

                    foreach (WhatsAppMessage msg in messages)
                    {
                        _logger.LogInformation(
                            "WhatsApp message received. From={From} Type={Type} MessageId={Id}",
                            msg.From,
                            msg.Type,
                            msg.Id);

                        batch.Add(new IncomingMessage
                        {
                            ConnectionId = connectionId,
                            OwnerUserId = ownerUserId,
                            MessageId = msg.Id,
                            Platform = MessagingPlatform.WhatsApp,
                            ExternalAccountId = entry.Id,
                            From = msg.From,
                            Timestamp = msg.Timestamp,
                            Type = msg.Type,
                            TextBody = msg.Text?.Body,
                            ChannelId = change.Value.Metadata.PhoneNumberId,
                            ChannelDisplayName = change.Value.Metadata.DisplayPhoneNumber,
                            ContactName = contactNames.GetValueOrDefault(msg.From),
                            ReceivedAt = DateTime.UtcNow,
                        });
                    }

                    await _publisher.PublishBatchAsync(batch, cancellationToken);
                }

                if (change.Value.Statuses is { Count: > 0 } statuses)
                {
                    foreach (WhatsAppMessageStatus status in statuses)
                    {
                        _logger.LogInformation(
                            "WhatsApp status update. MessageId={Id} Status={Status}",
                            status.Id,
                            status.Status);
                    }
                }
            }
        }
    }
}
