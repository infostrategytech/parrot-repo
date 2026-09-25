namespace Parrot.Application.FeatureFlags;

public static class FeatureFlagKeys
{
    // On: sign-up sends a six-digit code and sign-in refuses unverified
    // accounts. Off: sign-up creates a confirmed account and signs the person
    // straight in, and sign-in lets unverified accounts through, confirming
    // them as it does. Off is for deployments with no working mail.
    public const string RequireEmailVerification = "RequireEmailVerification";
}
