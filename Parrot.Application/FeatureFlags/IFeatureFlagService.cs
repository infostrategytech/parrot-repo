namespace Parrot.Application.FeatureFlags;

public interface IFeatureFlagService
{
    /// <summary>
    /// The FeatureFlags row for <paramref name="key"/> if there is one, else the
    /// FeatureFlags:{key} setting, else <paramref name="defaultValue"/>.
    /// </summary>
    Task<bool> IsEnabledAsync(string key, bool defaultValue, CancellationToken cancellationToken = default);
}
