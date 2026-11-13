namespace Parrot.Domain.Enums;

/// <summary>
/// The type of business resource a source represents.
/// Distinct from <see cref="Channel"/>, which identifies the connection that authorises it.
/// </summary>
public enum BusinessSourceKind
{
    Profile = 1,
    Catalog = 2,
}
