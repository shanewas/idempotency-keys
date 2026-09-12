using Microsoft.AspNetCore.Http;

namespace IdempotencyKeys;

public sealed class IdempotencyKeysMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyOptions _opt;
    public IdempotencyKeysMiddleware(RequestDelegate next, IIdempotencyStore store, IdempotencyOptions opt)
    { _next = next; _store = store; _opt = opt; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) && !HttpMethods.IsPatch(ctx.Request.Method))
        { await _next(ctx); return; }
        if (!ctx.Request.Headers.TryGetValue(_opt.HeaderName, out var kv) || string.IsNullOrWhiteSpace(kv.ToString()))
        {
            if (_opt.RequireKey) { ctx.Response.StatusCode = 400; return; }
            await _next(ctx); return;
        }
        var key = kv.ToString().Trim();
        if (key.Length is < 1 or > 256) { ctx.Response.StatusCode = 400; return; }

        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();
        ctx.Request.Body.Position = 0;
        var fp = Fingerprint.Compute(ctx.Request.Method, ctx.Request.Path.Value ?? "/", body);
        var scope = _opt.ScopeKey(ctx);

        var (inserted, rec) = await _store.TryInsertInFlightAsync(key, scope, fp);
        if (!inserted)
        {
            if (rec.Fingerprint != fp) { ctx.Response.StatusCode = 422; return; }
            if (rec.Status == EntryStatus.InFlight) { ctx.Response.StatusCode = 409; return; }
            ctx.Response.StatusCode = rec.ResponseStatus;
            foreach (var h in rec.ResponseHeaders) ctx.Response.Headers[h.Key] = h.Value;
            await ctx.Response.Body.WriteAsync(rec.ResponseBody);
            return;
        }

        var origBody = ctx.Response.Body;
        await using var buf = new MemoryStream();
        ctx.Response.Body = buf;
        try
        {
            try
            {
                await _next(ctx);
            }
            catch
            {
                await _store.RemoveAsync(key, scope);
                throw;
            }
            var bytes = buf.ToArray();
            var headers = new Dictionary<string, string>();
            foreach (var h in ctx.Response.Headers) headers[h.Key] = h.Value.ToString();
            await _store.CompleteAsync(key, scope, ctx.Response.StatusCode, headers, bytes);
            buf.Position = 0;
            await buf.CopyToAsync(origBody);
        }
        finally { ctx.Response.Body = origBody; }
    }
}
