using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKeys;

public sealed class IdempotencyOptions
{
    public string HeaderName { get; set; } = "Idempotency-Key";
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);
    public bool RequireKey { get; set; } = false;
    public Func<HttpContext, string> ScopeKey { get; set; } = DefaultScope;
    public static string DefaultScope(HttpContext ctx)
    {
        var route = ctx.Request.Path.Value ?? "/";
        var user = ctx.User?.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name ?? "anon" : "anon";
        return route + "|" + user;
    }
}

public static class Fingerprint
{
    public static string Compute(string method, string path, byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + path + "\n" + bodyHash)));
    }
}

public enum EntryStatus { InFlight, Completed }

public sealed class IdempotencyRecord
{
    public string Key { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public EntryStatus Status { get; set; }
    public int ResponseStatus { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    public byte[] ResponseBody { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IIdempotencyStore
{
    Task<(bool inserted, IdempotencyRecord rec)> TryInsertInFlightAsync(string key, string scope, string fingerprint, CancellationToken ct = default);
    Task<IdempotencyRecord?> GetAsync(string key, string scope, CancellationToken ct = default);
    Task CompleteAsync(string key, string scope, int status, Dictionary<string, string> headers, byte[] body, CancellationToken ct = default);
    Task RemoveAsync(string key, string scope, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(TimeSpan ttl, CancellationToken ct = default);
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IdempotencyRecord> _map = new();
    private static string K(string k, string s) => s + "\0" + k;

    public Task<(bool, IdempotencyRecord)> TryInsertInFlightAsync(string key, string scope, string fp, CancellationToken ct = default)
    {
        var rec = new IdempotencyRecord { Key = key, Scope = scope, Fingerprint = fp, Status = EntryStatus.InFlight };
        return Task.FromResult(_map.TryAdd(K(key, scope), rec) ? (true, rec) : (false, _map[K(key, scope)]));
    }
    public Task<IdempotencyRecord?> GetAsync(string key, string scope, CancellationToken ct = default)
        => Task.FromResult(_map.TryGetValue(K(key, scope), out var r) ? r : null);
    public Task CompleteAsync(string key, string scope, int status, Dictionary<string, string> headers, byte[] body, CancellationToken ct = default)
    {
        if (_map.TryGetValue(K(key, scope), out var r)) { r.Status = EntryStatus.Completed; r.ResponseStatus = status; r.ResponseHeaders = headers; r.ResponseBody = body; }
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string key, string scope, CancellationToken ct = default)
    {
        _map.TryRemove(K(key, scope), out _);
        return Task.CompletedTask;
    }
    public Task<int> PurgeExpiredAsync(TimeSpan ttl, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        int n = 0;
        foreach (var kv in _map) if (kv.Value.CreatedAt < cutoff && _map.TryRemove(kv.Key, out _)) n++;
        return Task.FromResult(n);
    }
}
