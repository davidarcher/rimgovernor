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
        FormatsOnceAtTheExactLimit();
        OverflowFormatsOnlyAfterRemoval();
        SaturationRefusesBoundedly();
        CancellationAndFailureReleaseCapacity();
        ShutdownFailsTheCapture();
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
                return ProtoBoundary.EncodeBounded(value, new List<Func<int>>(), () => value, out _);
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

    // A failure whose compact payload is exactly bytes UTF-8 bytes long,
    // built from two-byte characters so the byte and char counts differ.
    private static Common.Failure Sized(int bytes)
    {
        var failure = new Common.Failure { Code = Common.FailureCode.CapacityExhausted, Detail = "" };
        var overhead = Utf8.GetByteCount(ProtoBoundary.Format(failure, compact: true));
        var fill = bytes - overhead;
        failure.Detail = new string('é', fill / 2) + new string('x', fill % 2);
        return failure;
    }

    private static void FormatsOnceAtTheExactLimit()
    {
        var exact = Sized(ProtoBoundary.MaximumEnvelopeBytes);
        var hop = ObservationWork.Begin();
        var envelope = ProtoBoundary.EncodeBounded(exact, new List<Func<int>>(), () => new Common.Failure { Detail = "oversized" }, out var fits);
        ObservationWork.End();
        var payload = (string)envelope["payload"];
        Check(fits && Utf8.GetByteCount(payload) == ProtoBoundary.MaximumEnvelopeBytes, "exactly one MiB fits");
        Check(payload.Length < ProtoBoundary.MaximumEnvelopeBytes, "the bound is bytes, not characters");
        Check(hop.FormatPasses == 1 && hop.PayloadBytes == ProtoBoundary.MaximumEnvelopeBytes, "formatted once and measured from that string");
        Check(Failure(payload).Detail == exact.Detail, "the fitting payload is the reply");

        var over = Sized(ProtoBoundary.MaximumEnvelopeBytes + 1);
        hop = ObservationWork.Begin();
        envelope = ProtoBoundary.EncodeBounded(over, new List<Func<int>>(), () => new Common.Failure { Detail = "oversized" }, out fits);
        ObservationWork.End();
        Check(!fits && Failure((string)envelope["payload"]).Detail == "oversized", "one byte over is the typed capacity reply");
        Check(hop.FormatPasses == 2, "the oversized reply and its failure each format once");
    }

    // Drops run in order until the reply fits; one that removed nothing is
    // not a reason to format again, and later drops are not taken.
    private static void OverflowFormatsOnlyAfterRemoval()
    {
        var reply = Sized(ProtoBoundary.MaximumEnvelopeBytes + 10);
        var calls = new List<string>();
        var drops = new List<Func<int>>
        {
            () => { calls.Add("empty"); return 0; },
            () => { calls.Add("optional"); reply.Detail = "trimmed"; return 3; },
            () => { calls.Add("census"); return 4; },
        };
        var hop = ObservationWork.Begin();
        var envelope = ProtoBoundary.EncodeBounded(reply, drops, () => new Common.Failure { Detail = "oversized" }, out var fits);
        ObservationWork.End();
        Check(fits && Failure((string)envelope["payload"]).Detail == "trimmed", "the trimmed reply is returned");
        Check(string.Join(",", calls) == "empty,optional", "drops stop once the reply fits");
        Check(hop.FormatPasses == 2 && hop.DroppedSections == 3, "one extra pass, after the one actual removal");
    }

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
}
