using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.MessageBus;
using Parrot.Api.Security;

namespace Parrot.Api.Realtime;

/// <summary>
/// A stand-in for the message worker until it publishes real
/// <c>messages.persisted</c> events: publishes the handover's sample event,
/// addressed to the signed-in user, through the real bus. Everything
/// downstream (the realtime consumer, the router, SignalR) runs exactly as it
/// will for a real message. Mapped in Development only.
/// </summary>
public static class DevelopmentRealtimeEndpoints
{
    public sealed record StubRequest(string? Text, string? ConversationKey);

    public static void MapDevelopmentRealtimeStub(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapPost("/api/dev/realtime/persisted", async (
            StubRequest? request, ClaimsPrincipal user, IMessageBus bus, IOptions<KafkaSettings> kafka, CancellationToken ct) =>
        {
            string owner = user.WorkspaceId()!;
            string conversationKey = request?.ConversationKey ?? "whatsapp:2348012345678:2349011112222";
            DateTime now = DateTime.UtcNow;

            MessagePersisted persisted = new()
            {
                Source = MessagingPlatform.WhatsApp,
                CorrelationId = Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString(),
                Data = new StoredMessage
                {
                    MessageId = $"whatsapp:wamid.stub-{Guid.NewGuid():N}",
                    ConversationKey = conversationKey,
                    // Increasing, as the worker's per-conversation counter will be.
                    Sequence = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                    Platform = MessagingPlatform.WhatsApp,
                    OwnerUserId = owner,
                    Outgoing = false,
                    Sender = "2348012345678",
                    Receiver = "2349011112222",
                    SenderName = "Ada",
                    Type = "text",
                    Text = request?.Text ?? "Hello",
                    SentAt = now,
                    PersistedAt = now,
                },
            };

            await bus.PublishAsync(kafka.Value.PersistedTopic, conversationKey, persisted, ct);
            return Results.Accepted(value: persisted);
        })
        .RequireAuthorization()
        .ExcludeFromDescription();
    }
}
