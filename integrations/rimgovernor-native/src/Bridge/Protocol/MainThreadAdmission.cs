#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;

namespace HomeBridge.BridgeTools
{
    // Orders ProtoBoundary.OnMainThread hops by admission class (#631). The
    // host runs queued main-thread invocations in arrival order once per
    // frame, so a renew or stop queued behind a burst of bundle reads waited
    // for every one of them. Every hop is queued here instead and the host
    // is handed one pump per hop; each pump, when the game thread runs it,
    // executes the highest-class hop still pending (control before
    // observation before the mirror poll (#795), arrival
    // order within a class) rather than
    // its own. Pumps equal hops, so every hop runs exactly once; a hop
    // whose caller cancelled while queued is completed cancelled and
    // skipped. A pump that finds the frame's allowance spent (#988) runs
    // nothing unless control is queued, and the frame boundary (Frame)
    // drains what stays queued, so every hop still runs exactly once.
    internal static class MainThreadAdmission
    {
        internal const string ClassArgument = "class";
        internal const string Control = "control", Observation = "observation", Mirror = "mirror";

        internal sealed class Hop
        {
            internal readonly int Rank;
            internal readonly Func<object> Body;
            internal readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly CancellationToken Token;
            internal readonly int QueueDepth;
            // When it was queued, on the allowance's clock (#988).
            internal readonly long QueuedAt = Clock();
            internal Hop(int rank, Func<object> body, CancellationToken token, int queueDepth)
            { Rank = rank; Body = body; Token = token; QueueDepth = queueDepth; }
        }

        private static readonly object Gate = new object();
        // One list per rank, arrival order within it.
        private static readonly List<Hop>[] Pending = { new List<Hop>(), new List<Hop>(), new List<Hop>() };

        // The rank of a caller's class argument: control first. An absent or
        // unknown class is observation, the bulk of typed traffic.
        internal static int RankOf(string? cls)
        {
            switch (cls)
            {
                case Control: return 0;
                case Mirror: return 2;
                default: return 1;
            }
        }

        internal static string ClassOf(int rank) => rank == 0 ? Control : rank == 2 ? Mirror : Observation;

        // Queues body under rank and hands the host one pump. The returned
        // task completes with the body's reply (or its exception), or
        // cancelled when token fires before the hop runs. queueDepth is how
        // many hops were pending when this one was queued.
        internal static Task<object> Enqueue(IRimBridgeContext ctx, int rank, Func<object> body, CancellationToken token, out int queueDepth)
        {
            Hop hop;
            lock (Gate)
            {
                queueDepth = 0;
                foreach (var list in Pending) queueDepth += list.Count;
                hop = new Hop(rank, body, token, queueDepth);
                Pending[rank].Add(hop);
            }
            if (token.CanBeCanceled)
                token.Register(() => hop.Completion.TrySetCanceled(token));
            Task<object> pump;
            try
            {
                pump = ctx.MainThread.InvokeAsync<object>(Pump, CancellationToken.None);
            }
            catch (Exception e)
            {
                Remove(hop);
                hop.Completion.TrySetException(e);
                return hop.Completion.Task;
            }
            // A pump the host drops without running (shutdown) must not
            // strand a hop: fail the oldest pending one so its caller learns.
            pump.ContinueWith(t =>
            {
                if (!t.IsFaulted && !t.IsCanceled) return;
                var stranded = Take();
                if (stranded == null) return;
                if (t.Exception != null) stranded.Completion.TrySetException(t.Exception.InnerExceptions);
                else stranded.Completion.TrySetCanceled();
            }, TaskContinuationOptions.ExecuteSynchronously);
            return hop.Completion.Task;
        }

        private static void Remove(Hop hop)
        {
            lock (Gate) Pending[hop.Rank].Remove(hop);
        }

        // The highest-class pending hop, or null.
        private static Hop? Take()
        {
            lock (Gate)
            {
                foreach (var list in Pending)
                {
                    if (list.Count == 0) continue;
                    var hop = list[0];
                    list.RemoveAt(0);
                    return hop;
                }
            }
            return null;
        }

        // Runs on the game thread once per queued hop: executes the best
        // pending hop that is still wanted. While the clock runs and the
        // frame's allowance is spent, a non-control hop stays queued for the
        // frame boundary to drain (#988).
        private static object Pump()
        {
            while (true)
            {
                if (Budgeted() && spent >= AllowanceTicks && !ControlPending()) return null!;
                var hop = Take();
                if (hop == null) return null!;
                // Cancelled while queued: counted so the observation report
                // separates work never done from work that ran (#642).
                if (hop.Completion.Task.IsCompleted) { FrameAccounting.Cancelled(); continue; }
                Run(hop);
                return null!;
            }
        }

        // The per-frame main-thread allowance for bridge hops while the
        // clock runs (#988). Hops that do not fit wait for a later frame
        // instead of stacking into one; control hops always run and are
        // charged, so reads defer behind them. Paused, or with no frame
        // boundary seen for StaleMs (hook missing, game unloading), nothing
        // is deferred. The allowance is RIMGOVERNOR_MAIN_THREAD_BUDGET_MS
        // when set to a number in [0.5, 100], else DefaultAllowanceMs.
        internal const string AllowanceVariable = "RIMGOVERNOR_MAIN_THREAD_BUDGET_MS";
        internal const double DefaultAllowanceMs = 4.0, StaleMs = 250;
        internal static Func<long> Clock = System.Diagnostics.Stopwatch.GetTimestamp;
        internal static long Frequency = System.Diagnostics.Stopwatch.Frequency;
        internal static long AllowanceTicks = Ticks(Allowance(Environment.GetEnvironmentVariable(AllowanceVariable)));
        private static bool running;
        private static long spent, frameAt;
        private static bool framed;
        // Session aggregates for the timing block.
        private static ulong frames, overrunFrames, deferredHops;
        private static long maxDeferTicks;

        internal static double Allowance(string? configured)
            => double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms >= 0.5 && ms <= 100 ? ms : DefaultAllowanceMs;

        private static long Ticks(double ms) => (long)(ms * Frequency / 1000.0);

        private static bool Budgeted() => running && framed && Clock() - frameAt < Ticks(StaleMs);

        // The frame boundary, on the game thread: a fresh allowance, then
        // queued control, then deferred hops while it lasts. At least one
        // deferred hop runs per frame so none starves under a spent allowance.
        internal static void Frame(bool clockRunning)
        {
            if (framed && spent > AllowanceTicks) overrunFrames++;
            running = clockRunning; framed = true; frameAt = Clock(); spent = 0; frames++;
            RunControl();
            var floor = true;
            while (floor || !running || spent < AllowanceTicks)
            {
                var hop = Take();
                if (hop == null) break;
                if (hop.Completion.Task.IsCompleted) { FrameAccounting.Cancelled(); continue; }
                deferredHops++; maxDeferTicks = Math.Max(maxDeferTicks, Clock() - hop.QueuedAt);
                Run(hop);
                floor = false;
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Frequency;

        // The allowance's session account, once the frame boundary has run a
        // hop a spent allowance left queued.
        internal static Dictionary<string, object?>? BudgetReport()
        {
            if (deferredHops == 0) return null;
            var report = new Dictionary<string, object?>
            {
                ["allowanceMs"] = Ms(AllowanceTicks),
                ["frames"] = frames,
                ["overrunFrames"] = overrunFrames,
                ["deferredHops"] = deferredHops,
                ["maxDeferMs"] = Ms(maxDeferTicks),
            };
            return report;
        }

        // Resets the allowance's state; probes only.
        internal static void ResetBudget(Func<long> clock, long frequency, double allowanceMs)
        {
            Clock = clock; Frequency = frequency; AllowanceTicks = Ticks(allowanceMs);
            running = framed = false; spent = frameAt = 0;
            frames = overrunFrames = deferredHops = 0; maxDeferTicks = 0;
        }

        // Whether a control hop is queued: a pump runs one past a spent
        // allowance.
        private static bool ControlPending()
        {
            lock (Gate) return Pending[0].Count > 0;
        }

        // Runs every queued control hop now, on the game thread, at the
        // frame boundary; returns how many ran. Their
        // pumps, when the host runs them, find the hops gone and return,
        // so every hop still runs exactly once.
        private static int RunControl()
        {
            var ran = 0;
            while (true)
            {
                Hop? hop;
                lock (Gate)
                {
                    if (Pending[0].Count == 0) return ran;
                    hop = Pending[0][0];
                    Pending[0].RemoveAt(0);
                }
                if (hop.Completion.Task.IsCompleted) { FrameAccounting.Cancelled(); continue; }
                Run(hop);
                ran++;
            }
        }

        private static void Run(Hop hop)
        {
            var began = Clock();
            try
            {
                var reply = hop.Body();
                hop.Completion.TrySetResult(reply);
            }
            catch (OperationCanceledException) { hop.Completion.TrySetCanceled(); }
            catch (Exception e) { hop.Completion.TrySetException(e); }
            finally { spent += Math.Max(0, Clock() - began); }
        }

        // Pending hops per class, for a status line.
        internal static int PendingCount()
        {
            lock (Gate)
            {
                var n = 0;
                foreach (var list in Pending) n += list.Count;
                return n;
            }
        }
    }
}
