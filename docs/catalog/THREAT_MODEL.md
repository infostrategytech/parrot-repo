# Catalog service threat model (STRIDE)

**Scope (Phase 1):** `Parrot.Catalog.Api` (write, read and AI tool surfaces), `Parrot.Catalog.Relay`,
`Parrot.Catalog.Tools`, the `catalog` Postgres schema, Redis, Kafka topic `catalog.events`.
Phase 2 adds OpenSearch and embeddings, Phase 3 ingestion; their sections below are
placeholders to be filled when they ship.

**Trust boundaries**

1. Internet / CRM backend / AI agent runtime → API (bearer JWT).
2. API → Postgres (four roles, RLS).
3. API → Redis (tenant-prefixed keys).
4. Relay → Kafka → in-repo consumers.
5. Operators → Tools CLI (migrator credentials).

**Assets:** tenant catalog data (truth), prices, audit log, product versions, signing key,
database credentials, Redis auth, event stream.

Legend: **S**poofing, **T**ampering, **R**epudiation, **I**nformation disclosure,
**D**enial of service, **E**levation of privilege.

## 1. API (all surfaces)

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| S: forged or replayed JWT | Signature, `iss`, `aud`, `exp`, `nbf` validated; HMAC key ≥ 32 chars enforced outside Development; OIDC mode uses provider JWKS with rotation | Token revocation before expiry is not supported; keep lifetimes short (60 min) |
| S: tenant chosen by the caller | `tenant_id` read from the token only; DTOs reject unknown members (`tenant_id` in a body is a 400); AI tools accept no tenant argument | — |
| T: request smuggles unknown fields or oversized data | Strict System.Text.Json (`UnmappedMemberHandling=Disallow`, `MaxDepth=16`), 1 MB Kestrel body cap, 64 KB attributes cap, regexes on keys/SKUs/slugs/currency/region, enum whitelists, page caps (100; 10 for AI) | — |
| T: SQL injection via filter DSL or search | Keys validated against the registry, operators fixed, values become Npgsql parameters, relation names are constants; EF LINQ elsewhere | Unit tests assert no key/value reaches SQL text |
| R: who changed what | `audit_log` append-only (grant + trigger) with actor, request id, before/after; `product_versions` immutable | Log retention policy to be set with ops |
| I: cross-tenant read through any endpoint | RLS (FORCE) keyed to `app.tenant_id` set from the token on connection open; reader role sees views only; tenant-isolation test suite covers every endpoint and tool | Relies on Npgsql pool reset; startup refuses `No Reset On Close` / multiplexing |
| I: internal fields leak to clients or LLM | Read DTOs exclude tenant_id, search_text, actors; AI DTOs strip further; errors outside Development are generic | — |
| D: scraping or runaway agent | Per-token and per-tenant fixed-window limits, tighter on search and AI tools; search bounded by LIMIT and index predicates | Limits are per instance; move to a shared store if horizontal scale makes that matter |
| E: scope escalation | Deny-by-default fallback policy; each endpoint names a scope; admin never implies ai-tools; publish requires `catalog:publish` | — |

## 2. Database

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| S/E: application role connecting as owner | Separate login users per role, created outside migrations; migrator is not a superuser; TLS required (`rds.force_ssl`), IAM auth in AWS | Local compose uses passwords for the four users; never reuse them elsewhere |
| T: writer edits history | `REVOKE UPDATE, DELETE` on `audit_log` and `product_versions` for the writer; BEFORE trigger refuses even for the owner unless `app.allow_purge=on` inside the purge tool | — |
| T: overlapping or malformed prices | `EXCLUDE USING gist` on (offer, currency, type, period); CHECKs on amount, currency, validity | — |
| I: RLS bypass through views | Views run as owner under FORCE RLS; superuser ownership is forbidden by the bootstrap script; test asserts the reader cannot read base tables and sees no other tenant via views | — |
| I: missing tenant setting | `current_tenant_id()` returns NULL → zero rows; relay role has an explicit policy only on outbox/inbox | Tools require `--tenant` |
| D: unbounded tables | Outbox sweep (7 days), soft delete + purge job, index rebuilt per product not globally | Partitioning of `audit_log` if volume demands |
| E: migration drift | Migration history table in the `catalog` schema; unit test checks the security SQL covers every table | — |

## 3. Cache (Redis)

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| I: tenant A served B's cached page | Key = tenant id + generation + route + request hash; tenant from token; ETags are per response | — |
| T: stale data after a write | Generation bump from outbox events; TTL 5 min safety net; `as_of` never cached | Staleness window = relay lag; alert on `catalog.outbox.lag_seconds` |
| S: unauthenticated Redis | ElastiCache with auth token + TLS in Terraform; compose Redis is private to the network | — |

## 4. Events (outbox, Kafka, consumers)

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| T: lost or duplicated events | Outbox row in the business transaction; at-least-once relay; consumers dedupe on `event_id` via `consumer_inbox` | — |
| I: payload leaks across tenants | Envelope carries `tenant_id`; payloads carry ids and statuses, not free text; consumers scope by tenant | Topic ACLs per consumer group (MSK IAM) are a TODO in Terraform |
| D: poison message | Consumer logs, seeks back and retries after 1 s; relay records `attempts`/`last_error` and moves on | Add a dead-letter path when a consumer needs it |
| S: rogue producer on the topic | Kafka auth (SASL/IAM in AWS); local broker is network-private | — |

## 5. AI tool surface (prompt injection and misuse)

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| T: tenant text instructs the model | Free text returned only inside `untrusted_text` objects; control and invisible characters stripped; length capped; every tool description and response carries the data-not-instructions notice | The agent runtime must keep tool output in the tool-result role, never in the system prompt |
| S: model invents or chooses the tenant | No tenant argument exists; token decides | — |
| T: model invents facts | Tools return `data_as_of` and deterministic `why_matched`; the documented flow routes every fact through a tool | — |
| D: model loops on search | `limit ≤ 10`, tighter rate limit on `/ai/tools` | — |
| I: raw DB or search access | None; the tool API is the only path and uses the reader role | — |

## 6. Ingestion (Phase 3, placeholder)

Presigned uploads with size and content-type checks, malware scan, parsing in an isolated
worker, registry-driven mapping that is always reviewable, approval before writing drafts,
CSV formula-injection escaping on export. To be detailed when built.

## 7. Search projection (Phase 2, placeholder)

OpenSearch index per tenant prefix or tenant field with document-level security; mapping
generated from the registry; semantic re-rank only over deterministically filtered
candidates. To be detailed when built.

## 8. Operations and supply chain

| Threat | Mitigation | Residual / follow-up |
| --- | --- | --- |
| Secrets in images or repo | Secrets Manager / environment only; appsettings carry placeholders; Development refuses nothing but production refuses the placeholder key | Add SAST + dependency scanning + SBOM to CI (TODO) |
| Compromised container | Non-root `$APP_UID`, read-only root filesystem, diagnostics disabled, no shell tools needed | Image signing / provenance (TODO) |
| Noisy failure | Structured JSON logs with request id and tenant id, no PII; OTel traces (HTTP, Npgsql); metrics for search latency, cache hit rate, outbox lag, RLS errors | Alarms are a Terraform TODO |
| Tenant offboarding | `purge-tenant` runs under RLS with the tenant set, in one transaction, with an explicit confirm argument; audit log optionally retained | Retention schedule to be agreed |

## Change log

- **Phase 1 (2026-10-04):** initial model.
