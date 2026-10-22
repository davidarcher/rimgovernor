using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HomeBridge.BridgeTools;
using RimGovernor.Brain.HttpChannel;

// The actual shared dispatcher and idle account, with fake operation bodies.
internal static class NativeIdleExitProbe
{
    private sealed class Native : INativeOpInvoker
    {
        public IReadOnlyCollection<string> Names => new[] { "typed", "test/fixture" };
        public Task<string> InvokeAsync(string op, string body, CancellationToken ct) => Task.FromResult("{}");
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    internal static void Invoke() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        long now = 0;
        IdleExit.Timestamp = () => now;
        var limit = TimeSpan.FromMinutes(20);
        void Age() => now += 21 * 60 * Stopwatch.Frequency;
        var routes = new RouteTable(IdleExit.Request);
        var native = new Native();
        NativeRoutes.Register(routes, native);
        NativeRoutes.RegisterHealth(routes);
        NativeRoutes.RegisterToolList(routes, native);
        Func<CancellationToken, Task<ChannelResponse>> player = _ => Task.FromResult(ChannelResponse.Json(200, "{}"));
        // Player routes register on the same table, after native startup.
        routes.Map("GET", "/api/player/colony", (_, ct) => player(ct));
        var dispatcher = new Dispatcher(routes, 8192);
        Task<ChannelResponse> Call(string path, CancellationToken ct = default, string host = "localhost") =>
            dispatcher.DispatchAsync(new RequestHead { Host = host, Method = path.StartsWith("/native/", StringComparison.Ordinal) ? "POST" : "GET",
                Target = path, ContentType = "application/json", ContentLength = 0 }, new Dictionary<string, string>(), (_, _) => Task.FromResult(Array.Empty<byte>()), ct);
        foreach (var route in new[] { "/native/typed", "/native/test/fixture", "/api/player/colony" })
        {
            Check((await Call(route)).Status == 200, route + " succeeds");
            Check(!IdleExit.Due(limit), route + " resets idle time");
            Age();
            Check(IdleExit.Due(limit), route + " eventually expires");
        }
        foreach (var route in new[] { "/api/health", "/api/tools", "/missing" })
        {
            await Call(route);
            Check(IdleExit.Due(limit), route + " cannot keep an abandoned game alive");
        }
        Check((await Call("/api/player/colony", host: "remote.example")).Status == 403, "guard rejects external request");
        Check(IdleExit.Due(limit), "rejected request is not activity");
        var completion = new TaskCompletionSource<ChannelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        player = _ => completion.Task;
        var pending = Call("/api/player/colony");
        Age();
        Check(!IdleExit.Due(limit), "outstanding player request prevents exit");
        await Call("/native/typed");
        Age();
        Check(!IdleExit.Due(limit), "overlapping native completion cannot release player request");
        completion.SetResult(ChannelResponse.Json(200, "{}"));
        await pending;
        Check(!IdleExit.Due(limit), "completion starts fresh interval");
        Age();
        Check(IdleExit.Due(limit), "completed request releases activity");
        foreach (var cancel in new[] { false, true })
        {
            player = ct => cancel ? Task.FromCanceled<ChannelResponse>(ct) : throw new InvalidOperationException("forced failure");
            Check((await Call("/api/player/colony", new CancellationToken(true))).Status == (cancel ? 408 : 500), "failure/cancellation response");
            Check(!IdleExit.Due(limit), "failure/cancellation ends activity");
            Age();
            Check(IdleExit.Due(limit), "failure/cancellation leaks no active count");
        }
        Check(IdleExit.Parse(new[] { "-rimgovernor-idle-exit=20" }) == limit, "limit unchanged");
        Console.WriteLine("native-idle-exit: shared dispatcher covers player/native/fixture requests; health, discovery, rejected requests, overlap, failure, cancellation and expiry passed");
    }
}
