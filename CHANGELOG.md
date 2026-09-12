# Changelog

## v0.1.2 — 2026-09-12
- LICENSE + README embedded in both packages. No code changes.

## v0.1.1 — 2026-09-12
- 5xx responses are not cached: the key is released so clients retry instead
  of replaying a stale 500.
- Fingerprint and scope cover method + path + query, not just path.
- DI wiring that matches the README: `AddIdempotencyKeys`,
  `AddNpgsqlIdempotencyStore`, `UseIdempotencyKeys`.
- 10 tests green.

## v0.1.0 — 2026-09-12
- `Idempotency-Key` middleware for POST/PATCH: 409 in-flight, verbatim replay
  when complete, 422 on body mismatch, passthrough without header.
- Postgres store (Npgsql): single-table schema, `ON CONFLICT` insert fence,
  TTL purge. `IDEM_PG` connection string runs the live round-trip test.
- In-memory store for tests and single-instance use.
- Failed handlers release the key so clients can retry (no stuck 409s).
- 8 xUnit tests green, including an 8-way parallel race test.
