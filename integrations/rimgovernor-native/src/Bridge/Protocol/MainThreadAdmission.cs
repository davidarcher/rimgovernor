#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;

namespace HomeBridge.BridgeTools
{
    // Orders ProtoBoundary.OnMainThread hops by admission class (#631). The
    // host runs queued main-thread invocations in arrival order once per
    // frame, so a renew or stop queued behind a burst of bundle reads waited
    // for every one of them. Every hop is queued here instead and the host
    // is handed one pump per hop; each pump, when the game thread runs it,
    // executes the highest-class hop still pending (control before
    // observation before media, arrival order within a class) rather than
    // its own. Pumps equal hops, so every hop runs exactly once; a hop
    // whose caller cancelled while queued is completed cancelled and
    // skipped. Hops that call the host's InvokeAsync directly (the legacy
    // home/* tools) stay outside this ordering.
    internal static class MainThreadAdmission
    {
        internal const string ClassArgument = "class";
        internal const string Control = "control", Observation = "observation", Media = "media";

        internal sealed class Hop
        {
            internal readonly int Rank;
            internal readonly Func<object> Body;
            internal readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly CancellationToken Token;
            internal readonly int QueueDepth;
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
                case Media: return 2;
                default: return 1;
            }
        }

        internal static string ClassOf(int rank) => rank == 0 ? Control : rank == 2 ? Media : Observation;

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
        // pending hop that is still wanted.
        private static object Pump()
        {
            while (true)
            {
                var hop = Take();
                if (hop == null) return null!;
                // Cancelled while queued: counted so the observation report
                // separates work never done from work that ran (#642).
                if (hop.Completion.Task.IsCompleted) { FrameAccounting.Cancelled(); continue; }
                try
                {
                    var reply = hop.Body();
                    hop.Completion.TrySetResult(reply);
                }
                catch (OperationCanceledException) { hop.Completion.TrySetCanceled(); }
                catch (Exception e) { hop.Completion.TrySetException(e); }
                return null!;
            }
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
