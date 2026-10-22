using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HomeBridge.BridgeTools;

// Compile the real request boundary and timer; only operation bodies are fake.
namespace HomeBridge.BridgeTools
{
    internal sealed partial class NativeOpTable
    {
        internal static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> Ops = new();
        internal static Func<CancellationToken, Task<string>> Fixture;
        private static Task<string> InvokeRegisteredAsync(string op, string body, CancellationToken ct) => Fixture(ct);
    }
}

internal static class NativeIdleExitProbe
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    internal static void Invoke() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        long now = 0;
        IdleExit.Timestamp = () => now;
        var limit = TimeSpan.FromMinutes(20);
        void Age() => now += 21 * 60 * Stopwatch.Frequency;
        var table = new NativeOpTable();
        NativeOpTable.Ops["typed"] = (_, _) => Task.FromResult("typed");
        NativeOpTable.Fixture = _ => Task.FromResult("fixture");
        foreach (var route in new[] { "typed", "fixture" })
        {
            await table.InvokeAsync(route, "", default);
            Check(!IdleExit.Due(limit), route + " resets idle time");
            Age();
            Check(IdleExit.Due(limit), route + " does not keep abandoned game alive");
        }

        // A native request may remain queued/in flight beyond the idle limit.
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeOpTable.Ops["typed"] = (_, _) => completion.Task;
        var pending = table.InvokeAsync("typed", "", default);
        Age();
        Check(!IdleExit.Due(limit), "outstanding typed request prevents exit");
        await table.InvokeAsync("fixture", "", default);
        Age();
        Check(!IdleExit.Due(limit), "finishing overlapping fixture does not release typed request");
        completion.SetResult("done");
        await pending;
        Check(!IdleExit.Due(limit), "completion starts a fresh idle interval");
        Age();
        Check(IdleExit.Due(limit), "completed request releases activity");

        foreach (var route in new[] { "typed", "fixture" })
        {
            foreach (var cancel in new[] { false, true })
            {
                Task<string> Fail(CancellationToken ct) => cancel
                    ? Task.FromCanceled<string>(ct) : throw new InvalidOperationException("forced failure");
                NativeOpTable.Ops["typed"] = (_, ct) => Fail(ct);
                NativeOpTable.Fixture = Fail;
                try
                {
                    await table.InvokeAsync(route, "", new CancellationToken(true));
                    throw new Exception("expected failure");
                }
                catch (InvalidOperationException) { Check(!cancel, "expected synchronous failure"); }
                catch (OperationCanceledException) { Check(cancel, "expected cancellation"); }
                Check(!IdleExit.Due(limit), "failure/cancellation completes activity");
                Age();
                Check(IdleExit.Due(limit), "failure/cancellation does not leak in-flight count");
            }
        }
        Check(IdleExit.Parse(new[] { "-rimgovernor-idle-exit=20" }) == limit, "existing limit unchanged");
        Console.WriteLine("native-idle-exit: typed/fixture activity, overlap, completion, failure, cancellation and idle expiry passed");
    }
}
