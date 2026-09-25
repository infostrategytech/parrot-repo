using MongoDB.Bson.Serialization;

namespace Parrot.Domain.Entities.Messages;

// Building a class map is what registers its "_t" discriminator. Relying on [BsonKnownTypes] alone
// defers that until the base map is first built, so a read can hit an unknown discriminator and throw.
// Every process that touches the messages collection calls this once at startup.
public static class MessageSerializationRegistry
{
    private static int registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1)
        {
            return;
        }

        BsonClassMap.LookupClassMap(typeof(MessageDocument));
        BsonClassMap.LookupClassMap(typeof(WhatsAppMessageDocument));
        BsonClassMap.LookupClassMap(typeof(FacebookMessageDocument));
    }
}
