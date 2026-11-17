namespace Parrot.Catalog.Domain.Enums;

// Every enum here is stored as snake_case text with a CHECK constraint and is
// serialised the same way on the wire (see ADR-002).
public enum ProductTypeKind
{
    Good,
    Service,
    Subscription,
    Bundle,
}

public enum ProductTypeStatus
{
    Active,
    Deprecated,
}

public enum AttributeDataType
{
    String,
    Text,
    Integer,
    Number,
    Boolean,
    Date,
    Enum,
    MultiEnum,
    Json,
}

public enum ProductStatus
{
    Draft,
    Review,
    Published,
    Retired,
}

public enum VariantStatus
{
    Active,
    Inactive,
}

public enum OfferStatus
{
    Draft,
    Active,
    Inactive,
}

public enum BillingPeriod
{
    OneTime,
    Monthly,
    Quarterly,
    Annual,
}

public enum PriceType
{
    List,
    Promo,
    Contract,
}

public enum PriceStatus
{
    Active,
    Inactive,
}

public enum AvailabilityStatus
{
    Active,
    Inactive,
}

// How the business supplies it: from stock on hand, made after the order, or booked for a time.
public enum AvailabilityMode
{
    InStock,
    MadeToOrder,
    Bookings,
}

public enum RelationshipType
{
    BundledWith,
    Requires,
    CompatibleWith,
    AlternativeTo,
    UpgradeTo,
    DowngradeTo,
    Upsell,
    CrossSell,
    Replaces,
}

public enum MediaKind
{
    Image,
    Video,
    Document,
}

public enum TenantStatus
{
    Active,
    Suspended,
    Offboarding,
}

public enum CatalogStatus
{
    Active,
    Inactive,
}

// A stock hold: kept for a payment link, then taken off stock when it's paid, or let go.
public enum StockHoldStatus
{
    Held,
    Committed,
    Released,
}
