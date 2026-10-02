using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Application.Messaging;

/// <summary>
/// How every message on the bus is written and read: camelCase names and the
/// platform as text, e.g. <c>{ "source": "whatsapp", "data": … }</c>. Every
/// producer and consumer uses these options, so all services agree on the
/// wire format.
/// </summary>
public static class MessageJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // The default encoder escapes every quote in a raw payload as \u0022 so
        // the JSON is safe inside HTML. These messages never reach a page, and
        // that escaping makes the data field about 1.5 times its size.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
