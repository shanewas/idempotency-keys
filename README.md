# idempotency-keys

Safe retries for POST and PATCH in ASP.NET Core, backed by Postgres.

Send the same `Idempotency-Key` twice and the second request replays the stored
response instead of running your handler again. Retried payments stop double
charging. Double-clicked forms stop creating two orders.

```csharp
builder.Services.AddIdempotencyKeys("Host=localhost;Database=shop", opt =>
{
    opt.Ttl = TimeSpan.FromHours(24);
});

app.UseIdempotencyKeys();
```

Run `Schema.sql` once against your database. That's the whole setup.

## What happens per request

A POST or PATCH arrives with an `Idempotency-Key` header. The middleware hashes
method + path + body (SHA-256), then inserts an in-flight row. `ON CONFLICT DO
NOTHING` decides the winner, so 8 parallel retries still execute your handler
once. Covered by `NParallelSameKey_OneExecutes_Rest409`.

- Same key, same body, handler still running: `409 Conflict`.
- Same key, same body, handler done: stored status, headers, and body replayed
  byte for byte. Your handler doesn't run again.
- Same key, different body: `422 Unprocessable Entity`. Somebody reused a key
  for a different payment, and you want to know.
- Handler throws: the key is released so the client can retry. Covered by
  `ThrowingHandler_KeyReleased_RetryExecutesAgain`.
- No header: passes through untouched, unless you set `RequireKey`.

Keys live under a scope of route + user by default. Override `ScopeKey` if your
routes carry the real identity somewhere else. Old rows get purged after `Ttl`
(`PurgeExpiredAsync`, wire it to a timer or cron).

## Postgres schema

One table, in `src/IdempotencyKeys.Postgres/Schema.sql`. Composite primary key
`(scope, key)`, response snapshot as `status + headers (jsonb) + body (bytea)`.

## Limits, stated plainly

- At-least-once handlers still need to be idempotent inside; this dedupes at
  the HTTP layer, it doesn't fix a handler that half-writes then throws.
- No dashboard, no multi-database support. Postgres only in v0.1.
- `IDEM_PG` env var runs the round-trip test against a real database. Without
  it, the suite runs against the in-memory store (7 of 8 tests).

## Why another one

`ikyriak/IdempotentAPI` (329 stars) targets SQL Server and cache stores and
went quiet in late 2024. `movsal08/idempotencykey` (Jul 2026) covers
.NET + Postgres but is brand new with no traction. This package exists to be
the grown-up option: Stripe-style 409-in-flight vs 200-replay distinction,
422 on fingerprint mismatch, race tests you can read, and response replay you
can verify byte for byte.

## License

MIT.
