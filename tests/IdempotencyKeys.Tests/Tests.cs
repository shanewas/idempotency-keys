using Microsoft.AspNetCore.Http;
using System.Text;

namespace IdempotencyKeys.Tests;

static class Ctx
{
    public static (DefaultHttpContext ctx, IdempotencyKeysMiddleware mw, RequestDelegate next, MemoryStream resp) Make(
        IIdempotencyStore store, IdempotencyOptions? opt = null, string body = "{}", int countCalls = 0)
    {
        int calls = 0;
        var resp = new MemoryStream();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST"; ctx.Request.Path = "/pay";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = resp;
        RequestDelegate next = async c => { Interlocked.Increment(ref countCalls); c.Response.StatusCode = 201; await c.Response.WriteAsync("done-" + body); };
        var mw = new IdempotencyKeysMiddleware(next, store, opt ?? new IdempotencyOptions());
        return (ctx, mw, next, resp);
    }
    public static DefaultHttpContext Fresh(string key, string body = "{}")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST"; ctx.Request.Path = "/pay";
        ctx.Request.Headers["Idempotency-Key"] = key;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }
}

public class RaceTests
{
    [Fact]
    public async Task NParallelSameKey_OneExecutes_Rest409()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        var gate = new TaskCompletionSource();
        RequestDelegate slow = async c => { Interlocked.Increment(ref execs); await gate.Task; c.Response.StatusCode = 201; await c.Response.WriteAsync("ok"); };
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var ctx = Ctx.Fresh("k-race");
            var mw = new IdempotencyKeysMiddleware(slow, store, opt);
            await mw.InvokeAsync(ctx);
            return ctx.Response.StatusCode;
        }).ToList();
        await Task.Delay(300);
        gate.SetResult();
        var codes = await Task.WhenAll(tasks);
        Assert.Equal(1, execs);
        Assert.Single(codes.Where(c => c == 201));
        Assert.Equal(7, codes.Count(c => c == 409));
    }
}

public class MismatchTests
{
    [Fact]
    public async Task DifferentBodySameKey_422()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        RequestDelegate next = async c => { execs++; c.Response.StatusCode = 200; await c.Response.WriteAsync("x"); };
        var c1 = Ctx.Fresh("k1", """{"a":1}""");
        await new IdempotencyKeysMiddleware(next, store, opt).InvokeAsync(c1);
        var c2 = Ctx.Fresh("k1", """{"a":2}""");
        await new IdempotencyKeysMiddleware(next, store, opt).InvokeAsync(c2);
        Assert.Equal(422, c2.Response.StatusCode);
        Assert.Equal(1, execs);
    }
}

public class ReplayTests
{
    [Fact]
    public async Task CompletedReplay_Verbatim_NoReexec()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        RequestDelegate next = async c => { execs++; c.Response.StatusCode = 201; c.Response.Headers["X-R"] = "1"; await c.Response.WriteAsync("receipt"); };
        var c1 = Ctx.Fresh("k2");
        await new IdempotencyKeysMiddleware(next, store, opt).InvokeAsync(c1);
        var c2 = Ctx.Fresh("k2");
        await new IdempotencyKeysMiddleware(next, store, opt).InvokeAsync(c2);
        Assert.Equal(1, execs);
        Assert.Equal(201, c2.Response.StatusCode);
        c2.Response.Body.Position = 0;
        Assert.Equal("receipt", await new StreamReader(c2.Response.Body).ReadToEndAsync());
        Assert.Equal("1", c2.Response.Headers["X-R"].ToString());
    }
    [Fact]
    public async Task InFlightReplay_409()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryInsertInFlightAsync("k3", "/pay|anon", "fp");
        var ctx = Ctx.Fresh("k3", "other");
        var opt = new IdempotencyOptions { ScopeKey = _ => "/pay|anon" };
        var rec = (await store.GetAsync("k3", "/pay|anon"))!;
        var realFp = Fingerprint.Compute("POST", "/pay", System.Text.Encoding.UTF8.GetBytes("other"));
        rec.GetType().GetProperty("Fingerprint")!.SetValue(rec, realFp);
        int execs = 0;
        RequestDelegate next = c => { execs++; return Task.CompletedTask; };
        await new IdempotencyKeysMiddleware(next, store, opt).InvokeAsync(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal(0, execs);
    }
}

public class FailureTests
{
    [Fact]
    public async Task ThrowingHandler_KeyReleased_RetryExecutesAgain()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        RequestDelegate flaky = c =>
        {
            execs++;
            if (execs == 1) throw new InvalidOperationException("boom");
            c.Response.StatusCode = 201;
            return c.Response.WriteAsync("recovered");
        };
        var mw = new IdempotencyKeysMiddleware(flaky, store, opt);
        var c1 = Ctx.Fresh("kf");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(c1));
        var c2 = Ctx.Fresh("kf");
        await mw.InvokeAsync(c2);
        Assert.Equal(2, execs);
        Assert.Equal(201, c2.Response.StatusCode);
    }
}

public class ServerErrorTests
{
    [Fact]
    public async Task FiveHundred_NotCached_RetryReexecutes()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        RequestDelegate flaky = c =>
        {
            execs++;
            c.Response.StatusCode = execs == 1 ? 500 : 200;
            return c.Response.WriteAsync(execs == 1 ? "boom" : "recovered");
        };
        var mw = new IdempotencyKeysMiddleware(flaky, store, opt);
        var c1 = Ctx.Fresh("k5");
        await mw.InvokeAsync(c1);
        Assert.Equal(500, c1.Response.StatusCode);
        var c2 = Ctx.Fresh("k5");
        await mw.InvokeAsync(c2);
        Assert.Equal(2, execs);
        Assert.Equal(200, c2.Response.StatusCode);
    }

    [Fact]
    public async Task QueryString_ScopesSeparately_NoCrossTalk()
    {
        var store = new InMemoryIdempotencyStore();
        var opt = new IdempotencyOptions();
        int execs = 0;
        RequestDelegate next = c => { execs++; c.Response.StatusCode = 200; return c.Response.WriteAsync("x"); };
        var mw = new IdempotencyKeysMiddleware(next, store, opt);
        var c1 = Ctx.Fresh("kq");
        c1.Request.QueryString = new QueryString("?a=1");
        await mw.InvokeAsync(c1);
        var c2 = Ctx.Fresh("kq");
        c2.Request.QueryString = new QueryString("?a=2");
        await mw.InvokeAsync(c2);
        Assert.Equal(200, c2.Response.StatusCode);
        Assert.Equal(2, execs);
    }
}

public class TtlTests
{
    [Fact]
    public async Task PurgeExpired_RemovesOld()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryInsertInFlightAsync("old", "s", "f");
        await Task.Delay(50);
        Assert.Equal(1, await store.PurgeExpiredAsync(TimeSpan.FromMilliseconds(10)));
        Assert.Null(await store.GetAsync("old", "s"));
    }
    [Fact]
    public async Task PurgeExpired_KeepsFresh()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryInsertInFlightAsync("fresh", "s", "f");
        Assert.Equal(0, await store.PurgeExpiredAsync(TimeSpan.FromHours(1)));
        Assert.NotNull(await store.GetAsync("fresh", "s"));
    }
}

public class PgTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IDEM_PG");
    [Fact]
    public async Task Pg_RoundTrip_WhenConnProvided()
    {
        if (Conn is null) return;
        var store = new Postgres.NpgsqlIdempotencyStore(Conn);
        await store.TryInsertInFlightAsync("pgk", "s", Fingerprint.Compute("POST", "/pay", []));
        await store.CompleteAsync("pgk", "s", 200, new() { ["X-A"] = "b" }, [1, 2]);
        var rec = (await store.GetAsync("pgk", "s"))!;
        Assert.Equal(EntryStatus.Completed, rec.Status);
        Assert.Equal([1, 2], rec.ResponseBody);
    }
}
