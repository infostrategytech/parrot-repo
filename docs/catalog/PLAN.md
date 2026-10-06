# Catalog service: delivery plan

Phase 1 is delivered in this repository as a separate deployable, `Parrot.Catalog.Api`,
with a worker (`Parrot.Catalog.Relay`), an operator CLI (`Parrot.Catalog.Tools`) and
its own test project (`Test.Parrot.Catalog`). It shares the Postgres cluster, the Kafka
broker and the JWT trust of the existing Parrot API, and nothing else.

## Phase 1 (this delivery)

| Area | What ships |
| --- | --- |
| Schema | `catalog.*` tables with `tenant_id` on every row, RLS + FORCE RLS on every table, NOLOGIN group roles (`catalog_writer`, `catalog_reader`, `catalog_outbox_relay`), append-only grants on `audit_log` and `product_versions`, `btree_gist` EXCLUDE on prices, closure table for categories, `security_invoker` views for the read role |
| Registry | `attribute_definitions` compiled into a `TypeSchema` that validates writes (unknown keys, types, required, 64 KB cap, enum whitelists, bounds, regex), renders JSON Schema + UI hints, drives the derived index, the search text and the canonical document |
| Write API | CRUD for types, attribute definitions, type versions, categories, products, variants, offers, prices, availability, relationships, media; publish / retire; strict DTOs that reject unknown fields; 1 MB body cap |
| Read API | products, offers, availability, related, document, `POST /search` with a whitelist-compiled filter DSL over `product_attribute_index`, `POST /compare`, cursor pagination, ETag / If-None-Match, `as_of`, Redis cache keyed by tenant + generation |
| AI tools | six narrow POST endpoints plus a published JSON-schema manifest; tenant from the token only, internal fields stripped, free text in delimited `untrusted_text` objects, `why_matched`, `data_as_of` |
| Events | transactional outbox, polling relay to Kafka (`catalog.events`), idempotent in-repo cache-invalidation consumer with an inbox table |
| Ops | Dockerfiles (non-root, read-only FS), compose additions (Redis, catalog API, relay, role bootstrap), Terraform skeleton, CI job with Postgres + Redis services |
| Tests | unit (registry, filter compiler, cursor codec, renderer, scopes), integration against a real Postgres (CRUD, publish transaction, index rebuild, price overlap, outbox), tenant isolation through every endpoint and every AI tool, authz deny-by-default |
| Docs | README with architecture, ADRs, THREAT_MODEL.md, generated OpenAPI document |

## Phase 2
OpenSearch projection built from the registry; store `catalog_documents.embedding vector(N)`
(table already exists without the column); embedder consumer; semantic re-rank inside
`search_catalog` over the deterministic candidate set.

## Phase 3
Ingestion: presigned S3 upload, scan, isolated parser worker, registry-driven column
mapping, diff/preview, approval, write as drafts; export with formula-injection escaping.

## Phase 4
Eligibility rules on offers; relationship traversal (upgrade paths, bundle expansion).

## Ambiguities resolved by assumption (flagged for review)

1. **Stack.** The spec lists TypeScript / Go / Python, but this repository is .NET 10 with
   EF Core, Npgsql, Kafka and HMAC JWTs issued by `Parrot.Api`. The catalog is built on that
   stack. See ADR-001 and ADR-003.
2. **Tenant identity.** Parrot has no tenant table today; each business account is one
   tenant. `Parrot.Api` now issues `tenant_id = user.Id` and the catalog scopes in its JWT.
   A later multi-user workspace only changes what `Parrot.Api` puts in that claim.
3. **Enums.** Stored as `text` with CHECK constraints rather than Postgres enum types
   (ADR-002), which keeps type-version migrations and raw SQL simple.
4. **media / documents.** One `media` table with a `kind` column (`image`, `video`,
   `document`) instead of two tables with identical shapes.
5. **Editing a published product.** Allowed; it rebuilds the derived data, writes a new
   `product_versions` snapshot and emits `ProductUpdated`, so the published truth is always
   versioned. Retired products are frozen.
6. **Phase-1 search `query`.** Postgres full-text search over name, SKU, description and
   `searchable` attribute values, rebuilt in the same transaction as the derived index.
   Sorting is limited to `relevance`, `name`, `published_at`, `created_at` until OpenSearch.
7. **Docs in git.** The repo ignores `*.md`; `.gitignore` gains a negation for
   `docs/catalog/` so the README, ADRs and threat model are committed.
