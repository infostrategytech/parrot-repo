# ADR-003: Tenant and scopes come from the verified JWT; HMAC trust with Parrot.Api, OIDC optional

**Status:** accepted (Phase 1)

## Context
The spec assumes OIDC JWTs from an external provider verified against JWKS. This repository
issues its own HMAC-SHA256 JWTs from `Parrot.Api` (`JwtSettings`), and the web app already
holds those tokens. There is no tenant model: a business account is a user row.

## Decision
- The catalog validates bearer tokens with `iss`, `aud`, `exp`, `nbf` and signature checks.
  Two modes, selected by `Catalog:Auth:Mode`:
  - `Symmetric` (default here): the same `JwtSettings` secret/issuer/audience as `Parrot.Api`.
  - `Oidc`: `Authority` + `Audience`; the JWT bearer handler fetches JWKS and rotates keys.
- Required claims: `tenant_id` (UUID) and `scope` (space separated) or `scp`. A token
  without `tenant_id` or without a catalog scope is rejected by the fallback policy. Deny by
  default: every endpoint names a scope policy.
- `Parrot.Api` now adds `tenant_id = user.Id` and `scope = "catalog:read catalog:write
  catalog:publish catalog:admin"` to the tokens it issues, because each business account is
  one tenant and its owner administers its catalog. AI agents get a service token carrying
  only `catalog:ai-tools` and the agent's tenant, minted by `Parrot.Catalog.Tools mint-token`
  for development and by the CRM backend in production.
- The tenant id is copied from the token into a scoped `TenantContext` by middleware and
  from there into the Postgres session (`app.tenant_id`), never from a body, query string
  or LLM argument.

## Consequences
- No new identity provider is needed to ship Phase 1; moving to Cognito/Auth0 later is a
  configuration change plus a claim mapping.
- Tokens issued before this change lack `tenant_id` and are refused by the catalog until the
  user signs in again.
