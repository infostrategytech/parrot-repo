# ADR-006: Redis read cache keyed by tenant and a per-tenant generation counter

**Status:** accepted (Phase 1)

## Context
Published reads must be cacheable in Redis with tenant-prefixed keys and invalidated from
outbox events. Tracking every key a tenant produced and deleting them one by one is racy
and expensive.

## Decision
- Key shape: `catalog:{tenant_id}:g{generation}:{resource}:{sha256(request)}`.
- `generation` is an integer stored at `catalog:{tenant_id}:gen`; the cache invalidator
  consumer `INCR`s it on `ProductPublished`, `ProductUpdated`, `ProductRetired`,
  `OfferChanged`, `PriceChanged`, `AvailabilityChanged`. Every key for the old generation
  becomes unreachable at once and expires by TTL.
- The API also bumps the generation itself right after any successful admin write
  (`WriteInvalidationFilter`), so a reader on the shared Redis sees its own writes at once.
  The event-driven bump remains for writes made by other processes (tools, migrations).
- Entries carry a short TTL (default 5 minutes) as a safety net if the relay is behind.
- `as_of` requests and requests from a token without `catalog:read` are never cached.
- The cache stores the serialised response plus its ETag, so a 304 does not touch Postgres.

## Consequences
- Invalidation cost is O(1) per event, regardless of how many keys exist.
- A write is visible to cached readers after the relay publishes and the consumer bumps the
  generation (sub-second locally) or after the TTL, whichever comes first.
