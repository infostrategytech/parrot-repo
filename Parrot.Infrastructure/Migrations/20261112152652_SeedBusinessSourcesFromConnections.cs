using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <summary>
    /// One-time backfill: seeds a BusinessSources Profile row (MetadataSyncStatus=Pending) for every
    /// active WhatsApp / Instagram / Facebook connection that does not already have one, so that
    /// businesses connected before this feature was deployed get their AI knowledge sources automatically.
    ///
    /// Enum constants used here:
    ///   Channel          — WhatsApp=1, Instagram=2, Facebook=3
    ///   BusinessSourceKind — Profile=1
    ///   MetadataSyncStatus — Pending=1
    ///
    /// The NOT EXISTS guard on (WorkspaceId, Channel, Kind, ExternalId) mirrors the unique index
    /// UQ_BusinessSources_Identity, so the migration is safe to run more than once and will never
    /// produce a duplicate row or violate the constraint.
    ///
    /// Down() removes exactly the rows this migration could have inserted, identified by joining back
    /// to the same connection tables on the same four-column key.  It does not touch rows created by
    /// application code for connections added after deploy.
    /// </summary>
    public partial class SeedBusinessSourcesFromConnections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── WhatsApp ────────────────────────────────────────────────────────
            // ExternalId = PhoneNumberId (the identifier the Graph API and the worker use).
            // Skip connections with no PhoneNumberId: they were started but never fully configured
            // and there is no stable external ID to register.
            migrationBuilder.Sql("""
                INSERT INTO "BusinessSources" (
                    "Id",
                    "WorkspaceId",
                    "Channel",          -- 1 = Channel.WhatsApp
                    "Kind",             -- 1 = BusinessSourceKind.Profile
                    "ExternalId",
                    "AuthorizationConnectionId",
                    "IsActive",
                    "MetadataSyncStatus",   -- 1 = MetadataSyncStatus.Pending
                    "MetadataSyncAttemptCount",
                    "CreatedAt",
                    "UpdatedAt"
                )
                SELECT
                    gen_random_uuid(),
                    c."WorkspaceId",
                    1,
                    1,
                    c."PhoneNumberId",
                    c."Id",
                    true,
                    1,
                    0,
                    NOW(),
                    NOW()
                FROM "WhatsAppConnections" c
                WHERE c."DisabledAt"    IS NULL
                  AND c."PhoneNumberId" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM   "BusinessSources" b
                      WHERE  b."WorkspaceId" = c."WorkspaceId"
                        AND  b."Channel"     = 1
                        AND  b."Kind"        = 1
                        AND  b."ExternalId"  = c."PhoneNumberId"
                  );
                """);

            // ── Instagram ───────────────────────────────────────────────────────
            // ExternalId = AccountId (the IG-scoped user id from the token exchange).
            // Skip connections with no AccountId: webhook is set up but the account
            // has not been linked yet.
            migrationBuilder.Sql("""
                INSERT INTO "BusinessSources" (
                    "Id",
                    "WorkspaceId",
                    "Channel",          -- 2 = Channel.Instagram
                    "Kind",             -- 1 = BusinessSourceKind.Profile
                    "ExternalId",
                    "AuthorizationConnectionId",
                    "IsActive",
                    "MetadataSyncStatus",   -- 1 = MetadataSyncStatus.Pending
                    "MetadataSyncAttemptCount",
                    "CreatedAt",
                    "UpdatedAt"
                )
                SELECT
                    gen_random_uuid(),
                    c."WorkspaceId",
                    2,
                    1,
                    c."AccountId",
                    c."Id",
                    true,
                    1,
                    0,
                    NOW(),
                    NOW()
                FROM "InstagramConnections" c
                WHERE c."DisabledAt" IS NULL
                  AND c."AccountId"  IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM   "BusinessSources" b
                      WHERE  b."WorkspaceId" = c."WorkspaceId"
                        AND  b."Channel"     = 2
                        AND  b."Kind"        = 1
                        AND  b."ExternalId"  = c."AccountId"
                  );
                """);

            // ── Facebook ────────────────────────────────────────────────────────
            // ExternalId = PageId (the numeric Page ID from the token exchange).
            // Skip connections with no PageId: webhook is set up but the page
            // has not been linked yet.
            migrationBuilder.Sql("""
                INSERT INTO "BusinessSources" (
                    "Id",
                    "WorkspaceId",
                    "Channel",          -- 3 = Channel.Facebook
                    "Kind",             -- 1 = BusinessSourceKind.Profile
                    "ExternalId",
                    "AuthorizationConnectionId",
                    "IsActive",
                    "MetadataSyncStatus",   -- 1 = MetadataSyncStatus.Pending
                    "MetadataSyncAttemptCount",
                    "CreatedAt",
                    "UpdatedAt"
                )
                SELECT
                    gen_random_uuid(),
                    c."WorkspaceId",
                    3,
                    1,
                    c."PageId",
                    c."Id",
                    true,
                    1,
                    0,
                    NOW(),
                    NOW()
                FROM "FacebookConnections" c
                WHERE c."DisabledAt" IS NULL
                  AND c."PageId"     IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM   "BusinessSources" b
                      WHERE  b."WorkspaceId" = c."WorkspaceId"
                        AND  b."Channel"     = 3
                        AND  b."Kind"        = 1
                        AND  b."ExternalId"  = c."PageId"
                  );
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove rows that this migration inserted, identified by the same four-column
            // join key used in Up().  Rows that existed before this migration (none — the
            // BusinessSources table was new in an earlier migration on this branch) or that
            // were created by application code for the same connection are also removed by
            // this rollback, which is correct: rolling back removes the feature entirely.

            // WhatsApp profiles seeded from active connections
            migrationBuilder.Sql("""
                DELETE FROM "BusinessSources" bs
                USING "WhatsAppConnections" c
                WHERE bs."WorkspaceId"               = c."WorkspaceId"
                  AND bs."AuthorizationConnectionId" = c."Id"
                  AND bs."Channel"                   = 1
                  AND bs."Kind"                      = 1
                  AND bs."ExternalId"                = c."PhoneNumberId";
                """);

            // Instagram profiles seeded from active connections
            migrationBuilder.Sql("""
                DELETE FROM "BusinessSources" bs
                USING "InstagramConnections" c
                WHERE bs."WorkspaceId"               = c."WorkspaceId"
                  AND bs."AuthorizationConnectionId" = c."Id"
                  AND bs."Channel"                   = 2
                  AND bs."Kind"                      = 1
                  AND bs."ExternalId"                = c."AccountId";
                """);

            // Facebook profiles seeded from active connections
            migrationBuilder.Sql("""
                DELETE FROM "BusinessSources" bs
                USING "FacebookConnections" c
                WHERE bs."WorkspaceId"               = c."WorkspaceId"
                  AND bs."AuthorizationConnectionId" = c."Id"
                  AND bs."Channel"                   = 3
                  AND bs."Kind"                      = 1
                  AND bs."ExternalId"                = c."PageId";
                """);
        }
    }
}
