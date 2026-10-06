using System.Text.Json.Serialization;

namespace Parrot.Application.DTOs.Integrations;

public record WhatsAppWebhookPayload
{
    [JsonPropertyName("object")]
    public required string Object { get; init; }

    [JsonPropertyName("entry")]
    public required List<WhatsAppEntry> Entry { get; init; }
}

public record WhatsAppEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("changes")]
    public required List<WhatsAppChange> Changes { get; init; }
}

public record WhatsAppChange
{
    [JsonPropertyName("value")]
    public required WhatsAppChangeValue Value { get; init; }

    [JsonPropertyName("field")]
    public required string Field { get; init; }
}

public record WhatsAppChangeValue
{
    [JsonPropertyName("messaging_product")]
    public required string MessagingProduct { get; init; }

    [JsonPropertyName("metadata")]
    public required WhatsAppMetadata Metadata { get; init; }

    [JsonPropertyName("contacts")]
    public List<WhatsAppContact>? Contacts { get; init; }

    [JsonPropertyName("messages")]
    public List<WhatsAppMessage>? Messages { get; init; }

    [JsonPropertyName("statuses")]
    public List<WhatsAppMessageStatus>? Statuses { get; init; }
}

public record WhatsAppMetadata
{
    [JsonPropertyName("display_phone_number")]
    public required string DisplayPhoneNumber { get; init; }

    [JsonPropertyName("phone_number_id")]
    public required string PhoneNumberId { get; init; }
}

// Meta does not always send a contact's profile or wa_id (a customer who uses
// a username has no wa_id). Requiring them failed the whole payload's model
// binding with a 400 before the controller ran, and the batch's messages were
// lost, so everything here is optional: the name is only a display nicety.
public record WhatsAppContact
{
    [JsonPropertyName("profile")]
    public WhatsAppProfile? Profile { get; init; }

    [JsonPropertyName("wa_id")]
    public string? WaId { get; init; }
}

public record WhatsAppProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public record WhatsAppMessage
{
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public WhatsAppTextBody? Text { get; init; }
}

public record WhatsAppTextBody
{
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

public record WhatsAppMessageStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    // Absent for a recipient who uses a username, as wa_id is.
    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; init; }

    /// <summary>Why a failed message failed. Meta sends it only with "failed".</summary>
    [JsonPropertyName("errors")]
    public List<WhatsAppStatusError>? Errors { get; init; }
}

// Every field optional, so a reason Meta words differently can never fail the
// payload's model binding and lose the batch.
public record WhatsAppStatusError
{
    /// <summary>Meta's error code, e.g. 131047 (more than 24 hours since the customer wrote).</summary>
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("error_data")]
    public WhatsAppStatusErrorData? ErrorData { get; init; }
}

public record WhatsAppStatusErrorData
{
    [JsonPropertyName("details")]
    public string? Details { get; init; }
}
