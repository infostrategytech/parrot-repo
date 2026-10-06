# ADR-005: Transactional outbox with a polling relay to Kafka

**Status:** accepted (Phase 1)

## Context
Every state change must reach search, the renderer/embedder and the cache invalidator
exactly as it was committed, with no dual-write window. The repository already runs Kafka.

## Decision
- Services insert an `outbox` row in the same transaction as the business write. Envelope:
  `event_id` (UUID, the row id), `tenant_id`, `aggregate_type`, `aggregate_id`, `type`,
  `schema_version`, `occurred_at`, `payload`.
- `Parrot.Catalog.Relay` polls `outbox` as `catalog_outbox_relay`:
  `SELECT ... WHERE published_at IS NULL ORDER BY created_at LIMIT N FOR UPDATE SKIP LOCKED`,
  publishes each row to topic `catalog.events` with key `tenant_id:aggregate_id` (so a
  product's events stay ordered), then sets `published_at`. Failures leave the row
  unpublished with `attempts` and `last_error`; the next poll retries. Delivery is
  at-least-once; consumers dedupe on `event_id`.
- Polling (not LISTEN/NOTIFY or logical decoding) because it needs no extra Postgres
  privileges or Aurora configuration, is trivially observable (`outbox_lag_seconds`), and
  the volume is low.
- In-repo consumer in Phase 1: the cache invalidator. It records `(consumer, event_id)` in
  `consumer_inbox` before acting, so a redelivered event is a no-op.
- For local development the relay can also run in-process (`Catalog:Relay:InProcess=true`).

## Consequences
- One extra table scan every poll interval (default 1 s), bounded by the partial index on
  `published_at IS NULL`.
- Published rows are kept for 7 days by default and swept by `Parrot.Catalog.Tools
  sweep-outbox`, so the table does not grow without bound.
