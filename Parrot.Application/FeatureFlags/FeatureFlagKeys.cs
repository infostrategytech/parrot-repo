namespace Parrot.Application.FeatureFlags;

public static class FeatureFlagKeys
{
    // On: sign-up sends a six-digit code and sign-in refuses unverified
    // accounts. Off: sign-up creates a confirmed account and signs the person
    // straight in, and sign-in lets unverified accounts through, confirming
    // them as it does. Off is for deployments with no working mail.
    public const string RequireEmailVerification = "RequireEmailVerification";

    // On: agents can make checkouts (create_cart), from which customers pay. The key keeps its
    // old name so existing config (FeatureFlags__PaymentLinks) still switches it. Off by default.
    public const string PaymentLinks = "PaymentLinks";
}
