# ADR-001: REST + OpenAPI for all three API surfaces

**Status:** accepted (Phase 1)

## Context
The catalog has three consumers with different shapes: an admin UI doing CRUD, the CRM
backend doing deterministic lookups, and an AI agent calling a narrow tool surface whose
input/output schemas must be published verbatim as LLM tool definitions. The repository
already runs a REST API (`Parrot.Api`) and a gRPC service (`Parrot.APi.Grpc`) behind nginx.

## Options
- **GraphQL** gives clients free-form field selection and nested traversal. That is the
  opposite of what the security section asks for: a whitelist-compiled filter DSL, fixed
  response shapes, hard caps on list sizes, and no query language the LLM can shape.
  Field-level authorisation and cost limiting would have to be rebuilt on top.
- **gRPC** fits service-to-service lookups but the two real consumers today are a browser
  admin UI and an LLM tool runtime, both JSON-native. A proto contract would be a second
  source of truth next to the JSON schemas the tools must publish anyway.
- **REST + OpenAPI** keeps one contract (the OpenAPI document), one validation path
  (System.Text.Json with unknown members rejected), and lets the AI tool schemas be served
  from the same types that validate the requests.

## Decision
REST for the write API (`/api/catalog/admin/...`), the read API (`/api/catalog/...`) and
the AI tool API (`/api/catalog/ai/tools/...`). The OpenAPI document is served at
`/openapi/v1.json` and a snapshot is committed under `docs/catalog/openapi.json`.
A gRPC facade for the read API can be added later as a thin adapter over the same
application services if an internal consumer needs it.

## Consequences
- One HTTP host, one auth pipeline, one rate limiter, one error envelope (the same
  `ErrorResponse` shape `Parrot.Api` uses).
- Cursor pagination and ETags are explicit in every list/read endpoint.
- Nested data (offers with prices, relationships) is returned as fixed sub-objects, never
  on demand.
