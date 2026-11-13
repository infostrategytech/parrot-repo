namespace Parrot.Domain.Enums;

/// <summary>
/// The channel (and thus the connection entity) that authorises a business source.
/// Distinct from <see cref="BusinessSourceKind"/>, which identifies the type of resource.
/// </summary>
public enum Channel
{
    WhatsApp  = 1,
    Instagram = 2,
    Facebook  = 3,
}
