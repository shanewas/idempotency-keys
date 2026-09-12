using System.Text.Json;
using Npgsql;

namespace IdempotencyKeys.Postgres;

public sealed class NpgsqlIdempotencyStore(string connString) : IIdempotencyStore
{
    public async Task<(bool inserted, IdempotencyRecord rec)> TryInsertInFlightAsync(string key, string scope, string fp, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO idempotency_keys (key, scope, fingerprint, status) VALUES (@k,@s,@f,'inflight') ON CONFLICT (scope,key) DO NOTHING RETURNING key", c);
        cmd.Parameters.AddWithValue("k", key); cmd.Parameters.AddWithValue("s", scope); cmd.Parameters.AddWithValue("f", fp);
        var ok = await cmd.ExecuteScalarAsync(ct) is not null;
        var rec = (await GetAsync(key, scope, ct))!;
        return (ok, rec);
    }
    public async Task<IdempotencyRecord?> GetAsync(string key, string scope, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT fingerprint,status,response_status,response_headers,response_body,created_at FROM idempotency_keys WHERE scope=@s AND key=@k", c);
        cmd.Parameters.AddWithValue("s", scope); cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new IdempotencyRecord
        {
            Key = key, Scope = scope,
            Fingerprint = r.GetString(0),
            Status = r.GetString(1) == "completed" ? EntryStatus.Completed : EntryStatus.InFlight,
            ResponseStatus = r.IsDBNull(2) ? 0 : r.GetInt32(2),
            ResponseHeaders = r.IsDBNull(3) ? new() : JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(3)) ?? new(),
            ResponseBody = r.IsDBNull(4) ? [] : (byte[])r.GetValue(4),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(5),
        };
    }
    public async Task CompleteAsync(string key, string scope, int status, Dictionary<string,string> headers, byte[] body, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE idempotency_keys SET status='completed',response_status=@st,response_headers=@h::jsonb,response_body=@b WHERE scope=@s AND key=@k", c);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("h", JsonSerializer.Serialize(headers));
        cmd.Parameters.AddWithValue("b", body);
        cmd.Parameters.AddWithValue("s", scope); cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task RemoveAsync(string key, string scope, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM idempotency_keys WHERE scope=@s AND key=@k", c);
        cmd.Parameters.AddWithValue("s", scope); cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<int> PurgeExpiredAsync(TimeSpan ttl, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM idempotency_keys WHERE created_at < now() - make_interval(secs => @s)", c);
        cmd.Parameters.AddWithValue("s", ttl.TotalSeconds);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
