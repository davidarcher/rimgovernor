using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using RimBridgeServer.Sdk;
using Common = RimGovernor.Protocol.Common;

// The detached reply boundary (#644): ProtoBoundary.CaptureOnMainThread reads
// on the game thread, EncodeDetached formats on a ReplyEncoder worker, and
// EncodeBounded formats a fitting reply once. The game thread here is this
// probe's own thread, pumped explicitly (FakeGameThread); worker ordering is
// proven with gates, never with sleeps. Waits carry a generous hang guard
// only, so a broken boundary fails instead of hanging.
internal static class NativeReplyEncoderProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    // The host's main-thread dispatcher as the SDK presents it: work queues
    // until the probe pumps it on its own thread, or is dropped as a
    // shutting-down host drops it.
    private sealed class FakeGameThread : IMainThread
    {
        private readonly Queue<Action> queued = new Queue<Action>();
        private readonly List<Action> dropped = new List<Action>();
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (queued)
            {
                queued.Enqueue(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
                dropped.Add(() => completion.TrySetCanceled());
            }
            return completion.Task;
        }
        internal int Pump()
        {
            var ran = 0;
            while (true)
            {
                Action next;
                lock (queued) { if (queued.Count == 0) return ran; next = queued.Dequeue(); }
                next();
                ran++;
            }
        }
        internal void Shutdown()
        {
            List<Action> pending;
            lock (queued) { queued.Clear(); pending = dropped.ToList(); dropped.Clear(); }
            foreach (var drop in pending) drop();
        }
    }

    private sealed class Context : IRimBridgeContext
    {
        internal Context(IMainThread main) { MainThread = main; }
        public IMainThread MainThread { get; }
        public Dictionary<string, object> Arguments => null;
        public string OperationId => "probe";
        public string CapabilityId => "rimgovernor/probe";
    }

    // Admission reads the caller's class from the raw arguments when it
    // queues the hop.
    private static void As(string cls)
    {
        BridgeCommon.Arguments = new Dictionary<string, object> { ["request"] = "{}", [MainThreadAdmission.ClassArgument] = cls };
    }

    private static T Settle<T>(Task<T> task, string name)
    {
        Check(task.Wait(Guard), name + " settles within the hang guard");
        return task.Result;
    }

    private static void SlotsReturn(string name)
    {
        Check(SpinWait.SpinUntil(() => ReplyEncoder.Available == ReplyEncoder.Slots, Guard), name + ": every encoder slot is released");
    }

    private static Dictionary<string, object> Observation(object envelope)
    {
        var timing = (Dictionary<string, object>)((Dictionary<string, object>)envelope)[ProtoBoundary.TimingField];
        return (Dictionary<string, object>)timing["observation"];
    }

    internal static void Invoke()
    {
        EncodesOffTheGameThreadWhileControlRuns();
        SaturationRefusesBoundedly();
        CancellationAndFailureReleaseCapacity();
        ShutdownFailsTheCapture();
        FrameAllowanceSpreadsReads();
        OptionalJobsShareTheAllowance();
        BridgeCommon.Arguments = null;
        Console.WriteLine("native-reply-encoder: " + checks + " checks passed");
    }

    // The encoder is held on a gate; a control hop queued after it runs on
    // the game thread and completes before the gate opens, the encoder is
    // not the game thread, and the source the capture copied is mutated
    // while the encoder owns the copy without changing the reply.
    private static void EncodesOffTheGameThreadWhileControlRuns()
    {
        var game = new FakeGameThread();
        var ctx = new Context(game);
        var gameThread = Thread.CurrentThread.ManagedThreadId;
        var source = new Common.Failure { Code = Common.FailureCode.Unavailable, Detail = "captured é" };
        var lease = Settle(ReplyEncoder.Reserve(CancellationToken.None), "reserve");
        Check(lease != null && ReplyEncoder.Available == ReplyEncoder.Slots - 1, "a reservation holds one slot");

        As(MainThreadAdmission.Observation);
        var captureThread = -1;
        var capture = ProtoBoundary.CaptureOnMainThread(ctx, () => { captureThread = Thread.CurrentThread.ManagedThreadId; return source.Clone(); }, CancellationToken.None);
        Check(game.Pump() == 1, "the capture is one game-thread hop");
        var captured = Settle(capture, "capture");
        Check(captureThread == gameThread && captured.Hop.GameThread == gameThread, "capture ran on the game thread");

        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var encoderThread = -1;
            var encode = ProtoBoundary.EncodeDetached(captured, lease, value =>
            {
                encoderThread = Thread.CurrentThread.ManagedThreadId;
                entered.Set();
                if (!release.Wait(Guard)) throw new TimeoutException("release gate");
                return ProtoBoundary.Encode(value, compact: true);
            }, CancellationToken.None);
            Check(entered.Wait(Guard), "the encoder started");
            Check(encoderThread != gameThread, "the encoder is not the game thread");

            As(MainThreadAdmission.Control);
            var control = ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(new Common.Failure { Detail = "renew" }), CancellationToken.None);
            Check(game.Pump() == 1, "the control hop runs while the encoder is held");
            var renew = (Dictionary<string, object>)Settle(control, "control hop");
            Check(((string)renew["payload"]).Contains("renew"), "the control hop answered");
            Check(!encode.IsCompleted, "the encode is still held after the control hop");

            source.Detail = "mutated after capture";
            release.Set();
            var envelope = (Dictionary<string, object>)Settle(encode, "encode");
            var payload = (string)envelope["payload"];
            Check(payload.Contains("captured é") && !payload.Contains("mutated"), "the in-flight reply is the captured copy");
            Check(Failure(payload).Detail == "captured é", "the payload is official ProtoJSON of the capture");
            var observation = Observation(envelope);
            Check((int)observation["formatPasses"] == 0, "no formatting on the game thread");
            var encodeBlock = (Dictionary<string, object>)observation["encode"];
            Check((int)encodeBlock["formatPasses"] == 1, "a fitting reply formats once, on the encoder");
            Check((double)encodeBlock["ms"] >= 0 && (double)encodeBlock["queueMs"] >= 0, "encode timing is reported apart");
            Check((long)observation["payloadBytes"] == Utf8.GetByteCount(payload), "the returned payload's own UTF-8 bytes");
            var timing = (Dictionary<string, object>)envelope[ProtoBoundary.TimingField];
            Check(timing.ContainsKey("executeMs") && (string)timing[MainThreadAdmission.ClassArgument] == MainThreadAdmission.Observation, "the capture hop's timing and class");
        }
        SlotsReturn("held encode");
    }

    private static Common.Failure Failure(string payload) => Common.Failure.Parser.ParseJson(payload);

    private static void SaturationRefusesBoundedly()
    {
        var held = new List<ReplyEncoder.Lease>();
        for (var i = 0; i < ReplyEncoder.Slots; i++) held.Add(Settle(ReplyEncoder.Reserve(CancellationToken.None), "reserve " + i));
        Check(ReplyEncoder.Available == 0, "every slot reserved");
        Check(Settle(ReplyEncoder.Reserve(CancellationToken.None, 0), "saturated reserve") == null, "a saturated encoder refuses rather than queueing");
        held[0].Dispose();
        held[0].Dispose();
        Check(ReplyEncoder.Available == 1, "a lease releases exactly once");
        held[1].Dispose();
        SlotsReturn("saturation");
    }

    // How a task settled once it has: its exceptions, empty for success.
    private static List<Exception> Outcome(Task task)
    {
        try { Check(task.Wait(Guard), "settles within the hang guard"); return new List<Exception>(); }
        catch (AggregateException e) { return e.Flatten().InnerExceptions.ToList(); }
    }

    private static bool WasCancelled(Task task) { var errors = Outcome(task); return errors.Count > 0 && errors.All(e => e is OperationCanceledException); }

    private static ProtoBoundary.Captured<Common.Failure> CaptureOne(string detail)
    {
        var game = new FakeGameThread();
        As(MainThreadAdmission.Observation);
        var capture = ProtoBoundary.CaptureOnMainThread(new Context(game), () => new Common.Failure { Detail = detail }, CancellationToken.None);
        game.Pump();
        return Settle(capture, "capture " + detail);
    }

    private static void CancellationAndFailureReleaseCapacity()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(WasCancelled(ReplyEncoder.Reserve(cancelled.Token)), "a cancelled reservation throws");
            Check(ReplyEncoder.Available == ReplyEncoder.Slots, "and reserves nothing");

            var lease = Settle(ReplyEncoder.Reserve(CancellationToken.None), "reserve");
            var ran = false;
            var skipped = ProtoBoundary.EncodeDetached(CaptureOne("cancelled"), lease, value => { ran = true; return ProtoBoundary.Encode(value); }, cancelled.Token);
            Check(WasCancelled(skipped) && !ran, "a caller cancelled before the worker starts skips the encode");
            SlotsReturn("cancelled encode");
        }

        var failing = Settle(ReplyEncoder.Reserve(CancellationToken.None), "reserve");
        var failed = ProtoBoundary.EncodeDetached(CaptureOne("failing"), failing, value => { throw new InvalidOperationException("encoder failed"); }, CancellationToken.None);
        var errors = Outcome(failed);
        Check(errors.Count == 1 && errors[0] is InvalidOperationException && errors[0].Message == "encoder failed", "an encoder failure reaches the caller as itself");
        SlotsReturn("failed encode");
    }

    // A host that drops its queued main-thread work (shutdown) settles the
    // capture without running it, and a capture that throws faults with its
    // own exception: neither leaves a hop pending.
    private static void ShutdownFailsTheCapture()
    {
        var game = new FakeGameThread();
        var ctx = new Context(game);
        As(MainThreadAdmission.Observation);
        var ran = false;
        var capture = ProtoBoundary.CaptureOnMainThread(ctx, () => { ran = true; return new Common.Failure(); }, CancellationToken.None);
        game.Shutdown();
        Check(Outcome(capture).Count > 0 && !ran, "a dropped capture fails without running");
        Check(MainThreadAdmission.PendingCount() == 0, "no hop is stranded");

        As(MainThreadAdmission.Observation);
        var throwing = ProtoBoundary.CaptureOnMainThread<Common.Failure>(ctx, () => { throw new InvalidOperationException("capture failed"); }, CancellationToken.None);
        game.Pump();
        var errors = Outcome(throwing);
        Check(errors.Count == 1 && errors[0].Message == "capture failed", "a failed capture reaches the caller as itself");
        Check(MainThreadAdmission.PendingCount() == 0, "no hop is left pending");
    }

    // The per-frame hop allowance (#988): while the clock runs, reads past
    // the allowance wait for later frame boundaries, none is dropped, and a
    // control hop runs regardless; paused or with a stale boundary, nothing
    // is deferred. Time is a fake clock each hop advances.
    private static void FrameAllowanceSpreadsReads()
    {
        long now = 1;
        var ran = 0;
        MainThreadAdmission.ResetBudget(() => now, 1000000, 1.0);
        try
        {
            var game = new FakeGameThread();
            var ctx = new Context(game);
            Func<string, Task<object>> hop = cls =>
            {
                As(cls);
                return ProtoBoundary.OnMainThread(ctx, () => { now += 600; ran++; return ProtoBoundary.Encode(new Common.Failure { Detail = cls }); }, CancellationToken.None);
            };

            MainThreadAdmission.Frame(true);
            var reads = Enumerable.Range(0, 5).Select(_ => hop(MainThreadAdmission.Observation)).ToList();
            game.Pump();
            Check(ran == 2, "two reads fit the running frame's allowance");
            Check(MainThreadAdmission.PendingCount() == 3, "the rest wait for a later frame");
            var control = hop(MainThreadAdmission.Control);
            game.Pump();
            Settle(control, "control past the allowance");

            MainThreadAdmission.Frame(true);
            Check(ran == 5, "the next boundary drains what its allowance admits");
            MainThreadAdmission.Frame(true);
            Check(ran == 6 && MainThreadAdmission.PendingCount() == 0, "every deferred read runs, none dropped");
            var report = MainThreadAdmission.BudgetReport();
            Check(report != null && (ulong)report["deferredHops"] == 3, "the report counts the hops the boundary drained");

            foreach (var read in reads) Settle(read, "deferred read");
            MainThreadAdmission.Frame(false);
            var paused = Enumerable.Range(0, 4).Select(_ => hop(MainThreadAdmission.Observation)).ToList();
            game.Pump();
            Check(ran == 10, "paused, no read is deferred");

            MainThreadAdmission.Frame(true);
            now += 1000000;
            var stale = Enumerable.Range(0, 4).Select(_ => hop(MainThreadAdmission.Observation)).ToList();
            game.Pump();
            Check(ran == 14, "without a recent frame boundary, no read is deferred");
        }
        finally
        {
            MainThreadAdmission.ResetBudget(System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency, MainThreadAdmission.DefaultAllowanceMs);
        }
    }

    private sealed class Job : IObservationJob
    {
        private readonly Action unit; private int left;
        internal string Reason = "";
        internal Job(Action unit, int units) { this.unit = unit; left = units; }
        public string Obsolete => null!;
        public bool Step() { unit(); return --left == 0; }
        public void Abandon(string reason) => Reason = reason;
    }

    // One allowance per frame (#995): optional observation units run after
    // the frame's hops and only with what they left, at least one unit per
    // frame (the floor), round-robin across jobs; a job past DeadlineFrames
    // is abandoned; the one report carries the optional fields.
    private static void OptionalJobsShareTheAllowance()
    {
        long now = 1;
        var ran = 0;
        MainThreadAdmission.ResetBudget(() => now, 1000000, 1.0);
        try
        {
            var game = new FakeGameThread();
            var ctx = new Context(game);
            var scheduler = ObservationScheduling.Shared;
            var order = new List<string>();
            var a = new Job(() => { now += 300; order.Add("a"); }, 100000);
            var b = new Job(() => { now += 300; order.Add("b"); }, 100000);
            Check(scheduler.TryAdd(a) && scheduler.TryAdd(b), "jobs queue");

            MainThreadAdmission.Frame(true);
            Check(order.Count == 4, "an idle frame spends the whole allowance on optional units");
            Check(order.SequenceEqual(new[] { "a", "b", "a", "b" }), "jobs run round-robin");

            As(MainThreadAdmission.Observation);
            order.Clear();
            var reads = Enumerable.Range(0, 2).Select(_ => ProtoBoundary.OnMainThread(ctx, () => { now += 1100; ran++; order.Add("hop"); return ProtoBoundary.Encode(new Common.Failure()); }, CancellationToken.None)).ToList();
            MainThreadAdmission.Frame(true);
            Check(order.SequenceEqual(new[] { "hop", "a" }), "the queued hop runs first; past the spent allowance the floor still runs one optional unit");
            game.Pump();
            Check(ran == 1, "a spent allowance defers the other hop");
            order.Clear();
            MainThreadAdmission.Frame(true);
            Check(order.SequenceEqual(new[] { "hop", "b" }), "the next frame drains the hop, then the floor unit, round-robin");

            for (var i = 0; i < (int)ObservationScheduler.DeadlineFrames; i++) MainThreadAdmission.Frame(true);
            Check(a.Reason == "expired" && b.Reason == "expired" && scheduler.Pending == 0, "unfinished jobs expire after DeadlineFrames");

            var report = MainThreadAdmission.BudgetReport();
            Check(report != null && (ulong)report["optionalUnits"] > 0 && (ulong)report["expired"] == 2, "one report carries the optional account");
            foreach (var read in reads) Settle(read, "read beside optional work");
        }
        finally
        {
            MainThreadAdmission.ResetBudget(System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency, MainThreadAdmission.DefaultAllowanceMs);
        }
    }
}
