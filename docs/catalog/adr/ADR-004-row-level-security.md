# ADR-004: Row Level Security with FORCE, session GUC set on connection open, separate DB roles

**Status:** accepted (Phase 1)

## Context
Every table carries `tenant_id`. Application-level `WHERE tenant_id = ?` is necessary but
not sufficient: one forgotten predicate leaks data. Postgres RLS enforces the predicate in
the database for every statement.

## Decision
- `ALTER TABLE ... ENABLE ROW LEVEL SECURITY` and `FORCE ROW LEVEL SECURITY` on every
  catalog table, so even the table owner (the migrator) is subject to the policy.
- Policy on every table:
  `USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)`
  `WITH CHECK (same)`. A missing setting yields NULL and matches nothing.
- `outbox` and `consumer_inbox` have an additional policy `TO catalog_outbox_relay USING
  (true)` so the relay can drain every tenant's events; it has no grants on anything else.
- The app sets the GUC with `SELECT set_config('app.tenant_id', $1, false)` from an EF Core
  `DbConnectionInterceptor` the moment a connection is opened, using the tenant from the
  verified token. Npgsql resets session state (`DISCARD ALL`) when a connection returns to
  the pool, so the setting cannot leak between requests. Startup refuses connection strings
  with `No Reset On Close=true` or `Multiplexing=true`, which would break that guarantee.
  Writes additionally run inside an explicit transaction.
- Roles: `catalog_migrator` owns the schema; `catalog_writer` has DML on base tables except
  UPDATE/DELETE on `audit_log` and `product_versions`; `catalog_reader` has SELECT on the
  `v_*` views only (created `WITH (security_invoker = true)` so RLS applies to the caller,
  not the view owner); `catalog_outbox_relay` has SELECT/UPDATE/DELETE on `outbox` (delete for
  the retention sweep) and SELECT/INSERT on `consumer_inbox`. These are NOLOGIN group roles created by the migration.
  Login users are created outside the migration (compose bootstrap, Terraform/RDS IAM) and
  `GRANT`ed membership, so no credential ever appears in a migration.

## Consequences
- The tenant-isolation test suite connects as real non-owner roles; a test passing while
  connected as a superuser would prove nothing, so the fixture creates the roles.
- Tools (seed, migrate-type-version, purge) must set the tenant explicitly before touching
  rows; `--tenant` is mandatory.
- Policy errors surface as `42501`; they are counted and alertable.
