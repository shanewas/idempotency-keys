# idempotency-keys — Specification

## 1. Problem & buyer
Retried POSTs (timeout, double-click, webhook redelivery) cause double charges / duplicate orders.
Buyer: backend/API devs, fintech builders. Author lived this at PayFlow.

## 2. Differentiation (verified 2026-09-12)
- Node side crowded (express-idempotency, idempotency-key-middleware). .NET side:
  `movsal08/idempotencykey` (0 stars, Jul 2026, single author) is the only direct
  .NET+Postgres rival; `ikyriak/IdempotentAPI` (329 stars) is SQL-Server/cache
  oriented and stale since Nov 2024. No dual-stack package exists.
- Beat angle: grown-up Stripe semantics the newborn lacks — in-flight replay
  returns 409 (not 200), request-fingerprint mismatch returns 422, response
  (status+headers+body) replayed verbatim, OpenAPI story, load-tested, documented.
- README names movsal08 head-to-head. No slop claims.

## 3. Stack
.NET 9 (ASP.NET Core middleware, Npgsql), Postgres, optional TS client types.
Single table. MIT.

## 4. v0.1 scope (<= ~1.5k LOC)
- `IdempotencyKeysMiddleware`: `Idempotency-Key` header intake, key validation.
- PG store: key, request fingerprint (SHA-256 of method+path+body hash), status,
  response snapshot, timestamps. ON CONFLICT fence for concurrent replays.
- Semantics: first request executes; concurrent replay -> 409; completed replay
  -> stored response verbatim; fingerprint mismatch -> 422; TTL purge job.
- Key scope: route + authenticated user by default, configurable.
- Tests: xUnit + Testcontainers Postgres — race test (N parallel same-key),
  mismatch test, TTL test, replay-fidelity test.
- Non-goals: no dashboard, no multi-DB, no billing, no Express port in v0.1
  (TS client types only if trivial).

## 5. Architecture (file tree)
- src/IdempotencyKeys/{Middleware,Store,Options,Fingerprint}.cs
- src/IdempotencyKeys.Postgres/{Schema.sql, NpgsqlStore}.cs
- tests/IdempotencyKeys.Tests/{Race,Replay,Mismatch,Ttl}Tests.cs
- README.md, CHANGELOG.md, LICENSE (MIT)

## 6. Anti-patterns
- No magic global keys without scope. No silent 200 on in-flight.
- No speculative abstractions (no IStoreFactoryFactory). One PG store.
- No invented benchmarks; load numbers only from runs in bench/.

## 7. Release criteria
- `dotnet test` green incl. race test. README quickstart <= 5 min (middleware
  registration snippet + SQL). CHANGELOG entry. git tag v0.1.0. Packed nupkg
  artifact in dist/. Rivals section present and honest.
