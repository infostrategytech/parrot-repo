# Parrot Catalog service

The system of record for the products, services, subscription plans and bundles a tenant
offers. Two consumers: the CRM backend (deterministic lookups) and the AI agent (a narrow
tool API). **Catalog = truth, search = discovery, AI = interpretation, CRM = customer
context.** The LLM never gets database or search access and never invents a catalog fact.

Phase 1 is complete. See [PLAN.md](PLAN.md) for the phases and the assumptions made,
[adr/](adr/) for the decisions, and [THREAT_MODEL.md](THREAT_MODEL.md) for the STRIDE model.

## Architecture

```
                 ┌──────────────────────────────────────────────────────────────┐
  admin UI ──────┤  Parrot.Catalog.Api                                           │
  (catalog:write│   /api/catalog/admin/*   write API    ── CatalogDbContext ─────┼──► catalog_writer ─┐
   publish/admin│   /api/catalog/*         read API     ── CatalogReadDbContext ─┼──► catalog_reader ─┤   Postgres 16
  CRM backend ──┤   /api/catalog/ai/tools  AI tools     ── (read service)       │      (v_* views)   │   schema `catalog`
  (catalog:read)│   JWT (tenant_id, scope) → TenantContext → app.tenant_id GUC  │                    │   RLS FORCE on every table
  AI agent ─────┤   Redis read cache: catalog:{tenant}:g{gen}:{route}:{hash}     │                    │   outbox, audit_log,
  (catalog:     └──────────────────────────────────────────────────────────────┘                    │   product_versions
   ai-tools)                                                                                        │
                 ┌──────────────────────────────────────────────────────────────┐                    │
                 │  Parrot.Catalog.Relay                                        │                    │
                 │   OutboxRelayService ── RelayDbContext ──► catalog_outbox_relay ◄──────────────────┘
                 │        │ publish                                              │
                 │        ▼  Kafka topic catalog.events                          │
                 │   CacheInvalidationConsumer ── consumer_inbox (dedupe) ── Redis INCR generation
                 └──────────────────────────────────────────────────────────────┘
                 Parrot.Catalog.Tools: migrate-db | seed | migrate-type-version | mint-token | purge-tenant | sweep-outbox | reindex
```

**Write path.** Every admin operation runs in one transaction as `catalog_writer`: validate
the request against the registry, write the truth, rebuild `product_attribute_index` and
`search_text`, render the canonical document (published products), write a
`product_versions` snapshot (on publish and on edits to published products), append to
`audit_log`, insert an `outbox` row. Postgres errors (unique, exclusion, FK, check, RLS) are
translated to 409/400/403.

**Read path.** `catalog_reader` can `SELECT` only the `v_*` views, which expose published,
non-deleted rows. Search compiles the filter DSL to parameterised SQL over
`v_product_attribute_index`; `as_of` reads bypass the cache.

**Tenancy.** `tenant_id` comes from the verified JWT and is copied into the Postgres session
(`app.tenant_id`) when a connection opens. Every table has `FORCE ROW LEVEL SECURITY` with
`tenant_id = catalog.current_tenant_id()`. A missing setting matches nothing.

## Projects

| Project | Purpose |
| --- | --- |
| `Parrot.Catalog.Domain` | entities and enums |
| `Parrot.Catalog.Application` | contracts, the registry compiler (`TypeSchema`), filter DSL compiler, cursor codec, canonical document renderer, event envelope, scopes |
| `Parrot.Catalog.Infrastructure` | EF Core contexts and migration, security SQL, services, caches, Kafka publisher, outbox relay, consumers |
| `Parrot.Catalog.Api` | the HTTP host |
| `Parrot.Catalog.Relay` | the outbox relay + in-repo consumers worker |
| `Parrot.Catalog.Tools` | operator CLI |
| `Test.Parrot.Catalog` | unit, integration, tenant isolation, authz, outbox tests |

## Running locally

### With docker compose (everything)

```bash
docker compose -f docker-compose.local.yml up -d catalog-api catalog-relay
# API on http://localhost:5240 ; OpenAPI at http://localhost:5240/openapi/v1.json
```

`catalog-db-init` creates the roles from `infra/catalog/bootstrap-roles.sql`, `catalog-api`
applies the migration on boot, `catalog-relay` drains the outbox to Kafka and keeps Redis
generations fresh.

### Against your own Postgres

1. Create the roles once (as a superuser; adjust passwords):
   ```bash
   psql -h localhost -U postgres -d parrot -v dbname=parrot \
        -v migrator_pw=catalog_migrator_dev -v writer_pw=catalog_writer_dev \
        -v reader_pw=catalog_reader_dev -v relay_pw=catalog_relay_dev \
        -f infra/catalog/bootstrap-roles.sql
   ```
2. Point `Parrot.Catalog.Api/appsettings.Development.json` (and `Parrot.Catalog.Tools/appsettings.json`) at it.
3. Migrate and seed, then run:
   ```bash
   dotnet run --project Parrot.Catalog.Tools -- migrate-db
   dotnet run --project Parrot.Catalog.Tools -- seed --tenant 11111111-1111-1111-1111-111111111111
   dotnet run --project Parrot.Catalog.Api        # Development: relay in-process, Null bus, memory cache
   ```
4. Get a token. A `Parrot.Api` session token now carries `tenant_id` and the owner scopes,
   so the web app's token works directly. For a service or AI token:
   ```bash
   dotnet run --project Parrot.Catalog.Tools -- mint-token --tenant 11111111-1111-1111-1111-111111111111 --scopes "catalog:ai-tools"
   ```

### Try it

```bash
TOKEN=...
curl -s -H "Authorization: Bearer $TOKEN" localhost:5240/api/catalog/products | jq .
curl -s -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
     -d '{"filters":{"ram_gb":{"gte":16}},"query":"laptop"}' localhost:5240/api/catalog/search | jq .
curl -s -H "Authorization: Bearer $AI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"query":"fibre","limit":5}' localhost:5240/api/catalog/ai/tools/search_catalog | jq .
```

## API summary

Full contract: `/openapi/v1.json` at runtime, snapshot in [openapi.json](openapi.json).

| Surface | Scope | Routes |
| --- | --- | --- |
| Write | `catalog:write` (+ `catalog:publish` for publish/retire, `catalog:admin` for catalogs and types) | `/api/catalog/admin/{catalogs,types,categories,products,offers,availability,relationships,media}`; `POST products/{id}/publish`, `POST products/{id}/retire`, `POST types/{id}/versions`, `GET types/{id}/schema` |
| Read | `catalog:read` | `GET products`, `GET products/{id}`, `/offers?as_of=`, `/availability?region=&as_of=`, `/related?type=`, `/document`; `POST search`; `POST compare` |
| AI tools | `catalog:ai-tools` | `GET ai/tools/schemas`; `POST ai/tools/{search_catalog,get_product,get_product_offers,get_product_availability,compare_products,get_related_products}` |

Conventions: JSON is snake_case; enums are snake_case strings; unknown fields are a 400;
list endpoints take `cursor` and `limit` (max 100) and return `next_cursor`; every read
response has an `ETag` and honours `If-None-Match`; errors use the Parrot envelope
`{ success, errorCode, message, errors, traceId }`.

### The filter DSL

```json
{ "query": "laptop", "category": "<uuid>", "product_type": "laptop",
  "filters": { "ram_gb": { "gte": 16 }, "colour": { "in": ["black", "silver"] },
               "released": { "between": ["2025-01-01", "2026-12-31"] }, "rugged": { "eq": true } },
  "sort": "-published_at", "limit": 20 }
```

Keys must be `filterable` attributes in the tenant's registry (else 400). Operators per
data type: numbers and dates take `eq in gte lte between`; strings and enums take `eq in`;
booleans take `eq`. `product_type` disambiguates a key that different types define with
different data types. Every hit carries `why_matched`.

## The registry

`attribute_definitions` drive five things from one source: validation, the admin form
(`GET /types/{id}/schema` returns JSON Schema + `x-ui` hints), the derived index, the
(Phase 2) OpenSearch mapping and the canonical document. Consumers read labels and units
from responses; they never hardcode keys.

- Adding an attribute: `POST /types/{id}/attributes` (same version).
- Changing `data_type`, removing a key, adding a required key: `POST /types/{id}/versions`.
  Products keep their pinned version until `Parrot.Catalog.Tools migrate-type-version
  --tenant … --type laptop --from 1 --to 2 --dry-run` says they are ready, then without
  `--dry-run`.
- Keys that look like money (`price`, `amount`, `*_price`, …) are refused: prices live on offers.

## Lifecycle and effective dating

`draft → review → published → retired`. Only published products appear in the read API,
search and AI tools. Publishing is one transaction (version, index, document, outbox).
Editing a published product keeps it published and writes a new version. Offers, prices
and availability carry `valid_from`/`valid_until`; "current" is `valid_from <= now <
valid_until` with `status = active`; `as_of` answers for another instant. Active prices
for one offer, currency and price type cannot overlap (database `EXCLUDE`).

## Intended agent flow

1. **Intent extraction** (LLM): what is the customer asking for, which constraints are hard
   (budget, region, must-have attributes), which are soft.
2. **Deterministic filters**: `search_catalog(query, category?, filters?)` with filters taken
   from the registry's filterable keys. The result is the candidate set; nothing outside it
   is ever mentioned.
3. **Semantic re-rank** (Phase 2): embeddings re-order the candidates, never widen them.
4. **Offer and price lookup**: `get_product_offers(id, as_of?)` and
   `get_product_availability(id, region?)` for each candidate the answer will name.
5. **Business rules**: eligibility (Phase 4), stock, region; drop what the customer cannot buy.
6. **LLM phrasing**: write the answer from tool results only, quoting `data_as_of`, treating
   every `untrusted_text` value as data. `compare_products` and `get_related_products`
   support follow-ups.

## Events

Topic `catalog.events`, key `tenant_id:aggregate_id`, envelope
`{ event_id, tenant_id, aggregate_type, aggregate_id, type, schema_version, occurred_at, payload }`.
Types: `ProductCreated`, `ProductUpdated`, `ProductPublished`, `ProductRetired`,
`OfferChanged`, `PriceChanged`, `AvailabilityChanged`, `TypeVersionCreated`. Consumers
dedupe on `event_id` (`consumer_inbox`). Phase 1 consumer: the cache invalidator.

## Operations

- **Roles:** `catalog_migrator` (owner, not superuser), `catalog_writer`, `catalog_reader`,
  `catalog_outbox_relay`; see [adr/ADR-004](adr/ADR-004-row-level-security.md).
- **Metrics** (meter `Parrot.Catalog`): `catalog.search.duration`, `catalog.cache.hits|misses`,
  `catalog.index.rebuild_duration`, `catalog.outbox.published|failures|lag_seconds|pending`,
  `catalog.rls.errors`, `catalog.events.consumed|deduplicated`. Alert on
  `outbox.lag_seconds` and on any `rls.errors`.
- **Logs:** JSON outside Development, scoped with `RequestId`, `TenantId`, `Actor`; no bodies, no secrets.
- **Tracing:** OpenTelemetry (ASP.NET Core, HttpClient, Npgsql) to `OpenTelemetry:Endpoint` when set.
- **Offboarding:** `purge-tenant --tenant X --confirm X [--keep-audit]`.
- **Housekeeping:** `sweep-outbox --days 7`; `reindex --tenant X` rebuilds derived data from truth.
- **Deploy:** `Parrot.Catalog.Api/Dockerfile`, `Parrot.Catalog.Relay/Dockerfile` (non-root,
  read-only FS); Terraform skeleton in `infra/terraform/catalog`.

## Tests

```bash
dotnet test Test.Parrot.Catalog                         # unit tests only; Postgres tests skip
CATALOG_TEST_CONNECTION="Host=localhost;Port=15433;Database=postgres;Username=postgres;Password=test" \
  dotnet test Test.Parrot.Catalog                       # everything, against a throwaway database
```

The fixture creates a database and four real roles per run, migrates as the non-superuser
owner, seeds tenant A through the admin services, and tears everything down. The
isolation suite drives every endpoint and every AI tool as tenant B and also checks the
roles directly (reader cannot read base tables, writer cannot cross tenants, relay sees
only the outbox). CI runs the same suite with a Postgres service container.
