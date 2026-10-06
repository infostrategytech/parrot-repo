# ADR-002: Relational core + JSONB attributes + derived typed index

**Status:** accepted (Phase 1)

## Context
Products of different types carry different attributes. The usual choices are EAV tables,
fully schemaless documents, or wide per-type tables. The spec rules out the first two.

## Decision
- Strongly typed columns for everything shared by all products (identity, catalog, type,
  category, SKU, name, slug, description, status, timestamps, actors).
- One `attributes jsonb` column per product and per variant, validated on every write
  against a `TypeSchema` compiled from `attribute_definitions` for the product's pinned
  `(product_type_id, type_version)`. Unknown keys, wrong types, missing required keys,
  values outside `validation`, and payloads over 64 KB are rejected. Money never appears in
  attributes; the validator rejects any key whose definition is missing from the registry,
  so a tenant cannot smuggle a `price` key in.
- `product_attribute_index` is a **derived** table, rebuilt inside the same transaction as
  every product/variant write, holding one row per filterable attribute value with the
  value in a typed column (`numeric_value`, `text_value`, `bool_value`, `date_value`).
  `multi_enum` fans out to one row per value. Composite indexes on
  `(tenant_id, attribute_key, <typed column>)` serve the filter DSL.
- `search_text` on products is also derived (name, SKU, description, `searchable`
  attribute values) and feeds a stored generated `tsvector` for Phase-1 full text search.
- Enum-like columns are `text` with CHECK constraints rather than Postgres enum types:
  adding a value is a plain CHECK change inside a transaction, and raw SQL in the filter
  compiler and views needs no casts.

## Consequences
- The index is never written by hand and never read as truth. If it drifted, a rebuild
  from `attributes` would restore it; `Parrot.Catalog.Tools reindex` does exactly that.
- Changing `data_type` of an attribute or removing one is a new `type_version`; products pin
  their version, and `migrate-type-version --dry-run` reports what would change.
