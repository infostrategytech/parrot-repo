using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// Builds the stored document for one platform. Registering a mapper per platform replaces the switch
// that previously threw for anything but WhatsApp, taking the shared Kafka consumer down with it.
public interface IMessageDocumentMapper
{
    MessagingPlatform Platform { get; }

    MessageDocument ToDocument(IncomingMessage message);
}

// Resolves the mapper for a message's platform.
public interface IMessageDocumentMapperFactory
{
    MessageDocument ToDocument(IncomingMessage message);
}
