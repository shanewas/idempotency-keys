# idempotency-keys

For API backends taking payments or orders: safe retries for POST and PATCH
in ASP.NET Core, backed by Postgres.

Send the same `Idempotency-Key` twice and the second request replays the stored
response instead of running your handler again. Retried payments stop double
charging. Double-clicked forms stop creating two orders.

```csharp
using IdempotencyKeys;

builder.Services.AddIdempotencyKeys(o => o.Ttl = TimeSpan.FromHours(24));
// ... your endpoints ...
app.UseIdempotencyKeys();
```

Install: `dotnet add package IdempotencyKeys`. That registers the in-memory
store, fine for one instance. For Postgres, add
`dotnet add package IdempotencyKeys.Postgres`, run the schema once:

```sh
psql "$CONN" -f src/IdempotencyKeys.Postgres/Schema.sql
```

then register it after `AddIdempotencyKeys`:

```csharp
using IdempotencyKeys.Postgres;

builder.Services.AddNpgsqlIdempotencyStore(connString);
```

## What happens per request

A POST or PATCH arrives with an `Idempotency-Key` header. The middleware hashes
method + path + query + body (SHA-256), then inserts an in-flight row.
`ON CONFLICT DO NOTHING` decides the winner, so 8 parallel retries still
execute your handler once. Covered by `NParallelSameKey_OneExecutes_Rest409`.

- Same key, same request, handler still running: `409 Conflict`.
- Same key, same request, handler done: stored status, headers, and body
  replayed byte for byte. Your handler doesn't run again.
- Same key, different body on the same endpoint: `422`. Somebody reused a key
  for a different payment, and you want to know.
- Handler throws, or answers 5xx: the key is released so the client can retry.
  A 500 from a sick downstream never becomes a permanent cached answer.
- Same key on a different endpoint or query: separate scope, runs normally.
  Keys never leak across routes.
- No header: passes through untouched, unless you set `RequireKey`.

Keys live under a scope of method + route + user by default. Override
`ScopeKey` if your identity lives somewhere else. Old rows get purged after
`Ttl` (`PurgeExpiredAsync`, wire it to a timer or cron).

## Postgres schema

One table (`src/IdempotencyKeys.Postgres/Schema.sql`), composite key
`(scope, key)`, response snapshot as `status + headers (jsonb) + body (bytea)`.

## Limits, stated plainly

- HTTP-layer dedupe only. A handler that half-writes then throws still needs
  to be idempotent inside; this package stops the second execution from
  starting, it doesn't repair the first.
- No dashboard, no multi-database support. Postgres or in-memory in v0.1.
- Live-database tests run when `IDEM_PG` is set; without it the suite runs
  against the in-memory store.

## Why another one

`ikyriak/IdempotentAPI` (329 stars) targets SQL Server and cache stores and
went quiet in late 2024. `movsal08/idempotencykey` (Jul 2026) covers .NET +
Postgres but is brand new with no traction. Difference here: 409 in-flight
instead of 200, 422 on fingerprint mismatch, byte-for-byte replay, 5xx never
cached — each covered by a named test you can read (`RaceTests`,
`MismatchTests`, `ServerErrorTests`, `ReplayTests`).

Part of a trilogy: webhook-guard (verify at ingress) → idempotency-keys
(dedupe) → pg-outbox (publish at egress).

## License

MIT.
