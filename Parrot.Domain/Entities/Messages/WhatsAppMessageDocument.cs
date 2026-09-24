using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

// Carries no fields of its own yet: everything WhatsApp stores today is common to all platforms.
// The discriminator still records provenance and gives WhatsApp-only data somewhere to go later.
[BsonDiscriminator("whatsapp")]
public sealed class WhatsAppMessageDocument : MessageDocument
{
}
