# Agent State Protocol — v1 staging contract

This directory is the language-neutral contract source for a later, separately extracted `agent-state-protocol`. It is intentionally limited to concepts already represented by Codex Control Center. It does not define an application protocol server, storage engine, or SDK.

All schemas use JSON Schema Draft 2020-12. Each top-level message has `schemaVersion: 1`. IDs are opaque strings; timestamps are RFC 3339 UTC strings ending in `Z`; enums are stable strings; unknown values are represented as `null` or an explicit `unknown` state, never as zero. Binary data and CLR-specific types are excluded.

## Schemas

- `common/ErrorDetail`: sanitized application error data. A future JSON-RPC transport still uses its required numeric protocol error code separately.
- `common/SecretReference`: opaque identifier and kind only. No credential bytes or protected-store paths.
- `common/CapabilityDescriptor`: capability state with a controlled scalar-only `extensions` map.
- `accounts/AccountDescriptor` and `AccountCollection`: redacted account metadata, not the persisted profile model.
- `accounts/AccountMetadataUpdate`: the currently supported user-selected nickname update; `expectedRevision` is an optional opaque concurrency token for owners that implement it.
- `credentials/CredentialDescriptor`: credential ownership/state metadata only.
- `providers/ProviderDescriptor`: generic provider identity, endpoint, capabilities, and optional secret reference; it contains no Codex routing or catalog-path concepts.
- `usage/UsageSnapshot`: source- and freshness-aware measurements that do not assume currency semantics.

`sessions` and `leases` are deferred until a real session lifecycle and owner are implemented.

## Strictness and evolution

Stable records use `additionalProperties: false` so drift is caught during validation. `CapabilityDescriptor.extensions` is the only intentional extension point in v1; its values are limited to JSON scalar values. Clients should ignore unknown extension keys they do not understand. Clients must also handle an unknown enum value as an unsupported/unknown state rather than crashing, even though the v1 schema enumerates values understood by this staging version.

Compatible evolution may add optional fields or independent schemas. Adding enum values is permitted only where clients are required to tolerate unknown values. Removing/renaming a field, changing requiredness incompatibly, or changing a field's meaning/type requires v2. A v1 schema's existing field semantics never change silently.

## Secret handling

General state messages contain secret references and credential metadata, never OAuth tokens, API keys, TOTP seeds, raw `auth.json`, decrypted vault blobs, or storage paths. `ErrorDetail` messages and string details are sanitized before serialization. Example identifiers and values are synthetic.

## Real read-only consumer

The existing local CLI account listing and fleet/status read path now projects the real `ProfileMetadata` model through `AccountDescriptorProjector` before consuming the display label. The legacy CLI JSON schemas and output remain unchanged; the staging DTO is used as an internal read-only projection, not as a migration of the full application. The projection deliberately omits email, account subject, credential fingerprint, vault details, and token-derived identifiers, matching the CLI's existing privacy policy.

## Future transport mapping (not implemented)

When a local state owner is introduced, use JSON-RPC 2.0 semantics:

- Requests contain `jsonrpc: "2.0"`, a stable `method`, and optional `params`; include `id` when a response is required.
- Responses contain `jsonrpc: "2.0"`, the matching `id`, and exactly one of `result` or `error`.
- Notifications omit `id` and receive no response.
- Method names beginning with `rpc.` are reserved. Prefer stable lower-case namespaces such as `accounts.list`, `accounts.get`, and `accounts.rename`; this staging does not finalize a complete method catalog.
- A JSON-RPC error keeps its required numeric `code`; `ErrorDetail` may be carried in `error.data`.

For a future stdio or named-pipe stream, frame UTF-8 JSON-RPC messages as newline-delimited JSON (NDJSON): one compact JSON text per line, with the newline as the delimiter. Do not pretty-print multi-line messages on the wire. No server, pipe, daemon, SQLite store, or lease manager is part of this staging.

Example future request:

```json
{"jsonrpc":"2.0","method":"accounts.list","params":{},"id":"request_demo_01"}
```

Example future notification:

```json
{"jsonrpc":"2.0","method":"usage.refresh","params":{"subjectId":"account_demo_01"}}
```
