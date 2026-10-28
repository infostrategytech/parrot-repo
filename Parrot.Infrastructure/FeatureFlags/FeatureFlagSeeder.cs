using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Parrot.Application.FeatureFlags;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.FeatureFlags;

// Writes each known flag's row at startup when it is missing, so every switch is in the database to be
// flipped there. An existing row is never touched: what someone set by hand stays set.
public static class FeatureFlagSeeder
{
    private static readonly FeatureFlag[] Defaults =
    [
        new()
        {
            Key = FeatureFlagKeys.RequireEmailVerification,
            Enabled = true,
            Note = "On: sign-up sends a six-digit code and sign-in refuses unverified accounts. Turn off only where mail cannot be sent.",
        },
    ];

    public static async Task SeedAsync(ApplicationDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            HashSet<string> present = (await db.FeatureFlags.AsNoTracking().Select(f => f.Key).ToListAsync(cancellationToken)).ToHashSet();
            FeatureFlag[] missing = Defaults.Where(f => !present.Contains(f.Key)).ToArray();
            if (missing.Length == 0) return;

            foreach (FeatureFlag flag in missing)
            {
                db.FeatureFlags.Add(new FeatureFlag { Key = flag.Key, Enabled = flag.Enabled, Note = flag.Note, UpdatedAt = DateTime.UtcNow });
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded feature flags: {Keys}", string.Join(", ", missing.Select(f => $"{f.Key}={f.Enabled}")));
        }
        catch (DbUpdateException ex)
        {
            // Another instance seeded the same row first; theirs stands.
            logger.LogInformation(ex, "Feature flags were seeded by another instance");
        }
        catch (Exception ex)
        {
            // The table not migrated yet, say: the configured default still applies, so start anyway.
            logger.LogWarning(ex, "Could not seed feature flags; the configured defaults apply until a row exists");
        }
    }
}
