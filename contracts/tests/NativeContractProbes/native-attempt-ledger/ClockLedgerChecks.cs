#nullable enable
using System;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Clock = RimGovernor.Protocol.Clock;
using Receipts = RimGovernor.Protocol.Receipts;
using Kind = HomeBridge.BridgeTools.NativeAttemptLedger.DecisionKind;

internal static class ClockLedgerChecks
{
    private const string Start = "rimgovernor.clock.v1.Clock/Start";
    private const string Renew = "rimgovernor.clock.v1.Clock/Renew";
    private const string Speed = "rimgovernor.clock.v1.Clock/ChangeSpeed";
    private static int checks;
    private static void Check(bool value, string detail) { checks++; if (!value) throw new Exception(detail); }
    private static void Throws(Action action, string detail)
    { bool threw = false; try { action(); } catch (ArgumentException) { threw = true; } catch (InvalidOperationException) { threw = true; } Check(threw, detail); }
    private static Common.Identity Identity() => new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static Common.ObservationContext Context() => new Common.ObservationContext { Identity = Identity(), Tick = 10, NativeGeneration = 3 };
    private static Authority.WritePrecondition Pre(ulong id = 1) => new Authority.WritePrecondition
        { Identity = Identity(), ExpectedGeneration = 3, Attempt = new Common.AttemptKey { ControllerSessionId = "controller", ActionId = "action", AttemptId = id } };
    private static Clock.StartRequest Request(ulong id = 1) => new Clock.StartRequest
        { Authority = Pre(id), Speed = Clock.Speed.Normal, LeaseMs = 1000, MaxTicks = 100, Policy = new Clock.WatchPolicy { Mode = Clock.WatchMode.Colony, HealthDropFraction = 0 } };
    private static Clock.Status Status() => new Clock.Status { Context = Context(), NeverStarted = new Clock.NeverStarted(), ActualPaused = true };
    internal static int Run()
    {
        var ledger = new NativeAttemptLedger(Identity());
        var request = Request();
        var fresh = ledger.InspectClock(Start, request);
        Check(fresh.Kind == Kind.New && fresh.Reply == null && fresh.Handle == null && ledger.Count == 0, "clock inspection never admits");
        // Owner guard removed by #52: the admission context identity is the remaining guard.
        var badIdentity = Context(); badIdentity.Identity.LoadToken = "different";
        Check(ledger.AdmitClock(Start, request, badIdentity).Kind == Kind.Refused && ledger.Count == 0, "clock identity guard consumes no capacity");
        var badContext = Context(); badContext.NativeGeneration++;
        Check(ledger.AdmitClock(Start, request, badContext).Kind == Kind.Refused && ledger.Count == 0, "clock admission requires exact generation");
        var original = request.Clone(); var admissionContext = Context();
        var admission = ledger.AdmitClock(Start, request, admissionContext);
        Check(admission.Kind == Kind.Admitted && admission.Handle != null && admission.Reply == null && ledger.Count == 1, "clock reservation shares real ledger");
        request.MaxTicks = 999; admissionContext.Tick = 900;
        var pending = ledger.InspectClock(Start, original);
        Check(pending.Kind == Kind.InFlight && pending.Reply!.Receipt.Uncertain != null && pending.Reply.Receipt.AdmittedContext.Tick == 10, "clock request and context are detached");
        Check(ledger.AdmitClock(Start, original, Context()).Kind == Kind.InFlight && ledger.Count == 1, "reentrant clock admission does not reserve twice");
        Check(ledger.LookupClock(original.Authority.Attempt, Context()).Receipt.Uncertain != null, "in-flight clock lookup remains correlated uncertainty");
        Check(ledger.Lookup(original.Authority.Attempt, Context()).Failure.Code == Common.FailureCode.AttemptConflict, "operation lookup cannot mistake clock attempt for missing work");
        Throws(() => ledger.FinishClockApplied(admission.Handle!, new Clock.Status()), "empty clock status cannot finalize applied");
        var staleStatus = Status(); staleStatus.Context.Tick = 9;
        Throws(() => ledger.FinishClockApplied(admission.Handle!, staleStatus), "pre-dispatch clock status cannot finalize applied");
        staleStatus = Status(); staleStatus.Context.Identity.MapId = 1;
        Throws(() => ledger.FinishClockUncertain(admission.Handle!, staleStatus, "wrong map"), "clock evidence cannot cross maps");
        var observed = Status();
        var receipt = ledger.FinishClockApplied(admission.Handle!, observed);
        Check(receipt.Applied.Status.Equals(observed) && receipt.AdmittedContext.Equals(Context()), "clock applied retains actual status and admitted context");
        observed.ActualPaused = false; receipt.AdmittedContext.Tick = 99;
        var replay = ledger.InspectClock(Start, original);
        Check(replay.Kind == Kind.Replay && replay.Reply!.Receipt.Applied.Status.ActualPaused && replay.Reply.Receipt.AdmittedContext.Tick == 10, "clock result and receipt mutations cannot change replay");
        replay.Reply!.Receipt.Attempt.AttemptId = 999;
        Check(replay.Reply!.Receipt.Attempt.AttemptId == 1, "clock decision replies are defensive clones");
        var later = Context(); later.Tick = 100; later.NativeGeneration = 999;
        Check(ledger.LookupClock(original.Authority.Attempt, later).Receipt.Equals(replay.Reply!.Receipt), "clock receipt survives later generation changes");
        Throws(() => ledger.FinishClockUncertain(admission.Handle!, null, "late"), "clock terminal result is immutable");
        Throws(() => new NativeAttemptLedger(Identity()).FinishClockApplied(admission.Handle!, Status()), "clock foreign handle refused");
        Check(ledger.LookupClock(Pre(999).Attempt, Context()).Unknown != null, "never-admitted clock attempt remains unknown");
        later = Context(); later.Identity.MapId = 1;
        Check(ledger.LookupClock(original.Authority.Attempt, later).Failure.Code == Common.FailureCode.StaleIdentity, "clock lookup cannot cross original map");
        later = Context(); later.Identity.LoadToken = "new";
        Check(ledger.LookupClock(original.Authority.Attempt, later).Failure.Code == Common.FailureCode.StaleIdentity, "clock lookup cannot cross load");
        Check(new NativeAttemptLedger(later.Identity).LookupClock(original.Authority.Attempt, later).Unknown != null, "new load never retains old clock attempts");
        Exception? threadError = null;
        var thread = new Thread(() => { try { ledger.LookupClock(original.Authority.Attempt, Context()); } catch (Exception error) { threadError = error; } });
        thread.Start(); thread.Join(); Check(threadError is InvalidOperationException, "clock ledger access remains main-thread affine");
        Equality(ledger, original);
        Variants(ledger);
        Capacity();
        return checks;
    }
    private static void Equality(NativeAttemptLedger ledger, Clock.StartRequest original)
    {
        foreach (var change in new Action<Clock.StartRequest>[] {
            r => r.Authority.ExpectedGeneration++, r => r.Authority.Identity.MapId = 1,
            r => r.Speed = Clock.Speed.Fast, r => r.LeaseMs++, r => r.MaxTicks++, r => r.Policy.ClearHealthDropFraction(),
            r => r.Policy.AcknowledgedHostileIds.Add("hostile") })
        {
            var changed = original.Clone(); change(changed);
            Check(ledger.InspectClock(Start, changed).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "changed clock typed value or presence conflicts");
        }
        var json = Clock.StartRequest.Parser.ParseJson(JsonFormatter.Default.Format(original));
        Check(ledger.InspectClock(Start, json).Kind == Kind.Replay, "official JSON roundtrip preserves clock retry identity");
        foreach (var change in new Action<Clock.StartRequest>[] {
            r => r.Authority.Attempt.AttemptId = 0, r => r.Authority.Attempt.ClearActionId(), r => r.Authority.Identity.ClearMapId(),
            r => r.Authority.ExpectedGeneration = 0, r => r.Authority.Identity.ColonyId = "\ud800" })
        {
            var changed = original.Clone(); change(changed);
            Check(ledger.InspectClock(Start, changed).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "invalid clock envelope refused");
        }
        var unknown = Clock.StartRequest.Parser.ParseFrom(original.ToByteArray().Concat(new byte[] { 0xf8, 0x07, 0x01 }).ToArray());
        Check(ledger.InspectClock(Start, unknown).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock unknown root binary fields rejected");
        unknown = original.Clone();
        unknown.Policy = Clock.WatchPolicy.Parser.ParseFrom(unknown.Policy.ToByteArray().Concat(new byte[] { 0xf8, 0x07, 0x01 }).ToArray());
        Check(ledger.InspectClock(Start, unknown).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock unknown nested binary fields rejected");
        Check(ledger.InspectClock(Start.ToLowerInvariant(), original).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock full method spelling is fixed");
        Check(ledger.InspectClock(Renew, original).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock request cannot impersonate another method");
        Check(ledger.InspectClock(Start, new Clock.OwnedRequest()).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "unguarded pause is outside admitted request union");
        Check(ledger.InspectClock(Start, new Receipts.LookupRequest()).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "other messages cannot be smuggled as clock requests");
        var repeated = Request(10); repeated.Policy.AcknowledgedHostileIds.Add(new[] { "first", "second" });
        ledger.AdmitClock(Start, repeated, Context());
        repeated.Policy.AcknowledgedHostileIds.Clear(); repeated.Policy.AcknowledgedHostileIds.Add(new[] { "second", "first" });
        Check(ledger.InspectClock(Start, repeated).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "clock repeated order is retained");
    }
    private static void Variants(NativeAttemptLedger ledger)
    {
        var epoch = new Clock.OwnedRequest { Identity = Identity(), Owner = new Clock.EpochOwner { ControllerSessionId = "controller", Epoch = 1 } };
        var renew = new Clock.RenewRequest { Authority = Pre(3), Epoch = epoch, LeaseMs = 1000 };
        var admission = ledger.AdmitClock(Renew, renew, Context());
        Check(admission.Kind == Kind.Admitted, "typed renew admitted");
        var uncertain = ledger.FinishClockUncertain(admission.Handle!, null, "unknown native result");
        Check(uncertain.Uncertain.LastObserved == null && ledger.InspectClock(Renew, renew).Kind == Kind.Replay, "renew uncertainty is retained");
        var changedRenew = renew.Clone(); changedRenew.Epoch.Owner.Epoch++;
        Check(ledger.InspectClock(Renew, changedRenew).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "original epoch ownership participates in retry equality");
        var speed = new Clock.SpeedRequest { Authority = Pre(4), Epoch = epoch, Speed = Clock.Speed.Superfast };
        admission = ledger.AdmitClock(Speed, speed, Context());
        Check(admission.Kind == Kind.Admitted, "typed speed change admitted");
        var evidence = Status();
        var receipt = ledger.FinishClockUncertain(admission.Handle!, evidence, "partially observed");
        evidence.ActualPaused = false;
        Check(receipt.Uncertain.LastObserved.ActualPaused && ledger.LookupClock(speed.Authority.Attempt, Context()).Receipt.Equals(receipt), "uncertain speed status is detached and retained");
        var conflictingSpeed = speed.Clone(); conflictingSpeed.Authority = renew.Authority.Clone();
        Check(ledger.InspectClock(Speed, conflictingSpeed).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "clock methods share attempt namespace");
        var max = Request(ulong.MaxValue);
        Check(ledger.AdmitClock(Start, max, Context()).Kind == Kind.Admitted, "clock uint64 maximum attempt is preserved");
    }
    private static void Capacity()
    {
        var ledger = new NativeAttemptLedger(Identity());
        for (ulong i = 1; i <= NativeAttemptLedger.Capacity; i++)
        {
            var admitted = ledger.AdmitClock(Start, Request(i), Context());
            if (admitted.Kind != Kind.Admitted) throw new Exception("clock capacity admission failed");
            ledger.FinishClockUncertain(admitted.Handle!, null, "not dispatched in test");
        }
        Check(ledger.Count == 4096, "fixed capacity");
        Check(ledger.InspectClock(Start, Request(4097)).Reply!.Failure.Code == Common.FailureCode.CapacityExhausted, "full ledger refuses overflow clock before effects");
        Check(ledger.InspectClock(Start, Request(1)).Kind == Kind.Replay, "capacity never evicts");
    }
}
