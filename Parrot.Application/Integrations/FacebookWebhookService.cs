using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// The connection a webhook event was routed to, flattened for message building.
public sealed record FacebookPageRegistration(Guid ConnectionId, string OwnerUserId, string PageId);

public sealed class FacebookWebhookService : IFacebookWebhookService
{
    private readonly FacebookSettings _settings;
    private readonly IFacebookConnectionRepository _connections;
    private readonly InboundMessagePublisher _publisher;
    private readonly IAppLogger<FacebookWebhookService> _logger;
    private readonly IFacebookCredentialProtector _protector;
    private readonly IDeliveryReceipts? _receipts;
    private readonly IReceiptNotifier? _notifier;

    // How long recording receipts may hold up the 200 Messenger is waiting for.
    private static readonly TimeSpan ReceiptBudget = TimeSpan.FromSeconds(1);

    public FacebookWebhookService(
        IOptions<FacebookSettings> settings,
        IFacebookConnectionRepository connections,
        InboundMessagePublisher publisher,
        IAppLogger<FacebookWebhookService> logger,
        IFacebookCredentialProtector protector,
        IDeliveryReceipts? receipts = null,
        IReceiptNotifier? notifier = null)
    {
        _settings = settings.Value;
        _connections = connections;
        _publisher = publisher;
        _logger = logger;
        _protector = protector;
        _receipts = receipts;
        _notifier = notifier;
    }

    public string GetWebhookUrl() =>
        $"{_settings.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/facebook";

    public async Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default)
    {
        if (_settings.SignatureMode == WebhookSignatureMode.Off)
        {
            return true;
        }

        // A manual connection's webhooks come from the customer's own Meta app and are
        // signed with its secret; everything else comes through Parrot's app.
        FacebookConnection? connection = string.IsNullOrWhiteSpace(identifier)
            ? null
            : await _connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        string source = connection?.AppSecretEncrypted is null ? "app" : "connection";
        string? secret = source == "app" && !string.IsNullOrWhiteSpace(_settings.AppSecret) ? _settings.AppSecret : null;
        string? reason = null;

        if (source == "connection")
        {
            try
            {
                secret = _protector.Unprotect(connection!.AppSecretEncrypted!);
            }
            catch (CryptographicException)
            {
                reason = "secret_unreadable";
            }
        }

        reason ??= secret is null ? "no_app_secret"
            : signature is null ? "missing_signature"
            : WebhookSignature.Matches(rawBody, signature, secret) ? null
            : "mismatch";
        if (reason is null)
        {
            return true;
        }

        if (_settings.SignatureMode == WebhookSignatureMode.LogOnly)
        {
            _logger.LogWarning(
                "Facebook webhook signature check failed; accepted because SignatureMode is LogOnly. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
                reason, source, connection?.Id);
            return true;
        }

        _logger.LogWarning(
            "Facebook webhook rejected because its signature check failed. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
            reason, source, connection?.Id);
        return false;
    }

    public async Task<bool> ProcessConnectionEventAsync(
        string identifier,
        FacebookWebhookPayload payload,
        string rawBody,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            _logger.LogWarning("Facebook webhook rejected because its connection reference is empty.");
            return false;
        }

        if (payload.Object is not ("page" or "instagram"))
        {
            _logger.LogWarning("Unrecognised Facebook webhook object type: {Object}", payload.Object);
            return false;
        }

        FacebookConnection? connection = await _connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null)
        {
            _logger.LogWarning("Facebook webhook rejected because its connection reference is unknown or disabled.");
            return false;
        }

        if (connection.WebhookVerifiedAt is null)
        {
            _logger.LogWarning("Facebook webhook rejected for unverified connection. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        if (string.IsNullOrEmpty(connection.PageId))
        {
            _logger.LogWarning("Facebook webhook rejected because no Page is linked. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        // For Messenger, entry.id is the Page ID. A mismatch means the ?ref= belongs to a different customer.
        if (payload.Entry.Count == 0 || payload.Entry.Any(entry => entry.Id != connection.PageId))
        {
            _logger.LogWarning(
                "Facebook webhook rejected because Page IDs do not match. ConnectionId={ConnectionId} Expected={Expected} Received={Received} DroppedEvents={DroppedEvents}",
                connection.Id,
                connection.PageId,
                string.Join(',', payload.Entry.Select(entry => entry.Id)),
                payload.Entry.Sum(entry => entry.Messaging?.Count ?? 0));
            return false;
        }

        FacebookPageRegistration registration = new FacebookPageRegistration(connection.Id, connection.OwnerUserId, connection.PageId!);

        _logger.LogInformation(
            "Facebook webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id,
            payload.Entry.Count);

        string correlationId = InboundMessagePublisher.CurrentCorrelationId();
        DateTime receivedAt = DateTime.UtcNow;
        List<InboundMessage> inbound = [];
        List<string> types = [];
        List<FacebookMessagingEvent> receipts = [];

        foreach (FacebookEntry entry in payload.Entry)
        {
            if (entry.Messaging is { Count: > 0 } messagingEvents)
            {
                foreach (FacebookMessagingEvent evt in messagingEvents)
                {
                    if (evt.Delivery is not null || evt.Read is not null)
                        receipts.Add(evt);

                    if (ToInbound(registration, evt, rawBody, correlationId, receivedAt) is { } message)
                    {
                        inbound.Add(message);

                        // The same type the Message Worker decodes, so received and persisted line up.
                        types.Add(evt.Message is null ? "postback"
                            : evt.Message.Attachments is { Count: > 0 } attachments ? attachments[0].Type : "text");
                    }
                }
            }

            if (entry.Changes is { Count: > 0 } changes)
                ProcessPageChanges(entry.Id, registration.OwnerUserId, changes);
        }

        await _publisher.PublishAsync(inbound, cancellationToken);

        // Counted only once safely queued, so a webhook that failed and is resent counts once.
        foreach (string type in types)
        {
            MessageMetrics.Received.Add(1, MessageMetrics.MessageType(type));
        }

        await RecordReceiptsAsync(registration, receipts, cancellationToken);
        return true;
    }

    // Delivered and read reports for replies the Page sent. Delivery names message ids and a watermark;
    // a read has only a watermark, so it covers every earlier reply to that customer. Never fails or
    // delays the webhook: it has one short budget, and what it cannot record is simply not shown.
    private async Task RecordReceiptsAsync(
        FacebookPageRegistration registration, List<FacebookMessagingEvent> events, CancellationToken cancellationToken)
    {
        if (_receipts is null || events.Count == 0)
        {
            return;
        }

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReceiptBudget);
        foreach (FacebookMessagingEvent evt in events)
        {
            if (budget.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // On a delivery or read event the sender is the customer.
                string customer = ConversationKey.NormalizeParticipant(MessagingPlatform.Facebook, evt.Sender.Id);
                if (evt.Delivery is { } delivery)
                {
                    DateTime? upTo = ProviderTimestamp.FromUnixMilliseconds(delivery.Watermark);
                    DateTime at = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp) ?? upTo ?? DateTime.UtcNow;
                    foreach (string mid in delivery.Mids ?? [])
                    {
                        await _receipts.RecordAsync(registration.ConnectionId, mid, DeliveryState.Delivered, at, budget.Token);
                    }

                    if (upTo is { } deliveredUpTo)
                    {
                        await _receipts.RecordUpToAsync(registration.ConnectionId, customer, DeliveryState.Delivered, at, deliveredUpTo, budget.Token);
                    }
                }

                // Instagram's equivalent carries a message id, not a watermark: nothing to read here.
                if (evt.Read is { } read && ProviderTimestamp.FromUnixMilliseconds(read.Watermark) is { } readUpTo)
                {
                    DateTime at = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp) ?? readUpTo;
                    await _receipts.RecordUpToAsync(registration.ConnectionId, customer, DeliveryState.Read, at, readUpTo, budget.Token);
                }

                // The open inbox re-reads the thread.
                if (_notifier is not null)
                {
                    await _notifier.ReceiptsChangedAsync(registration.OwnerUserId, registration.ConnectionId, customer, budget.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Facebook receipt not recorded. ConnectionId={ConnectionId} Reason={Reason}", registration.ConnectionId, ex.Message);
            }
        }
    }

    // A customer's message or postback becomes an InboundMessage; the body goes
    // on as received for the Message Worker to decode. Everything else is only
    // logged, without the customer's id or anything they typed.
    private InboundMessage? ToInbound(
        FacebookPageRegistration registration,
        FacebookMessagingEvent evt,
        string rawBody,
        string correlationId,
        DateTime receivedAt)
    {
        // Echoes are the Page's own replies coming back.
        if (evt.Message?.IsEcho == true)
        {
            return null;
        }

        string? mid = evt.Message?.Mid ?? evt.Postback?.Mid;
        if (mid is not null)
        {
            return new InboundMessage
            {
                MessageId = $"facebook:{mid}",
                Source = MessagingPlatform.Facebook,
                Sender = ConversationKey.NormalizeParticipant(MessagingPlatform.Facebook, evt.Sender.Id),
                Receiver = ConversationKey.NormalizeParticipant(MessagingPlatform.Facebook, registration.PageId),
                ConversationKey = ConversationKey.Create(MessagingPlatform.Facebook, evt.Sender.Id, registration.PageId),
                Data = rawBody,

                // Messenger sends milliseconds; WhatsApp sends whole seconds.
                SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp),
                ReceivedAt = receivedAt,
                CorrelationId = correlationId,
                ConnectionId = registration.ConnectionId,
                OwnerUserId = registration.OwnerUserId,
            };
        }

        if (evt.Delivery is not null)
        {
            _logger.LogInformation(
                "Facebook delivery confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Delivery.Watermark);
        }
        else if (evt.Read is not null)
        {
            _logger.LogInformation(
                "Facebook read confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Read.Watermark);
        }
        else if (evt.Reaction is not null)
        {
            _logger.LogInformation(
                "Facebook reaction. TenantId={TenantId} PageId={PageId} Mid={Mid} Action={Action}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Reaction.Mid,
                evt.Reaction.Action);
        }

        return null;
    }

    private void ProcessPageChanges(string pageId, string tenantId, List<FacebookChange> changes)
    {
        foreach (FacebookChange change in changes)
        {
            _logger.LogInformation(
                "Facebook page change. TenantId={TenantId} PageId={PageId} Field={Field}",
                tenantId, pageId, change.Field);
        }
    }
}
