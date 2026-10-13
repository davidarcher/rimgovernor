#nullable disable
using System;
using System.Linq;
using System.Threading;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Receipts = RimGovernor.Contracts.Wire.Receipts;
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
    private static void ThrowsJson(Action action, string detail)
    { bool threw = false; try { action(); } catch (System.Text.Json.JsonException) { threw = true; } Check(threw, detail); }
    private static Common.Identity Identity() => new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static Common.ObservationContext Context() => new Common.ObservationContext { Identity = Identity(), Tick = 10, NativeGeneration = 3 };
    private static Authority.WritePrecondition Pre(ulong id = 1) => new Authority.WritePrecondition
        { Identity = Identity(), ExpectedGeneration = 3, Attempt = new Common.AttemptKey { ControllerSessionId = "controller", ActionId = "action", AttemptId = id } };
    private static Clock.StartRequest Request(ulong id = 1) => new Clock.StartRequest
        { Authority = Pre(id), Speed = Clock.Speed.Normal, LeaseMs = 1000, MaxTicks = 100, Policy = new Clock.WatchPolicy { Mode = Clock.WatchMode.Colony, HealthDropFraction = 0 } };
    private static Clock.Status Status() => new Clock.Status { Context = Context(), State = new Clock.NeverStarted(), ActualPaused = true };
    internal static int Run()
    {
        var ledger = new NativeAttemptLedger(Identity());
        var request = Request();
        var fresh = ledger.InspectClock(Start, request);
        Check(fresh.Kind == Kind.New && fresh.Reply == null && fresh.Handle == null && ledger.Count == 0, "clock inspection never admits");
        // Admission validates the context identity and native generation.
        var badIdentity = Context() with { Identity = Identity() with { LoadToken = "different" } };
        Check(ledger.AdmitClock(Start, request, badIdentity).Kind == Kind.Refused && ledger.Count == 0, "clock identity guard consumes no capacity");
        var badContext = Context() with { NativeGeneration = 4 };
        Check(ledger.AdmitClock(Start, request, badContext).Kind == Kind.Refused && ledger.Count == 0, "clock admission requires exact generation");
        var original = request; var admissionContext = Context();
        var admission = ledger.AdmitClock(Start, request, admissionContext);
        Check(admission.Kind == Kind.Admitted && admission.Handle != null && admission.Reply == null && ledger.Count == 1, "clock reservation shares real ledger");
        // Records are immutable: a changed copy of the request or context cannot reach the admitted entry.
        var mutatedCopy = request with { MaxTicks = 999 }; var mutatedContext = admissionContext with { Tick = 900 };
        Check(mutatedCopy.MaxTicks == 999 && mutatedContext.Tick == 900, "changed copies are separate values");
        var pending = ledger.InspectClock(Start, original);
        Check(pending.Kind == Kind.InFlight && pending.Reply!.Receipt.Uncertain != null && pending.Reply.Receipt.AdmittedContext.Tick == 10, "clock request and context are detached");
        Check(ledger.AdmitClock(Start, original, Context()).Kind == Kind.InFlight && ledger.Count == 1, "reentrant clock admission does not reserve twice");
        Check(ledger.LookupClock(original.Authority.Attempt, Context()).Receipt.Uncertain != null, "in-flight clock lookup remains correlated uncertainty");
        Check(ledger.Lookup(original.Authority.Attempt, Context()).Failure.Code == Common.FailureCode.AttemptConflict, "operation lookup cannot mistake clock attempt for missing work");
        Throws(() => ledger.FinishClockApplied(admission.Handle!, new Clock.Status()), "empty clock status cannot finalize applied");
        var staleStatus = Status() with { Context = Context() with { Tick = 9 } };
        Throws(() => ledger.FinishClockApplied(admission.Handle!, staleStatus), "pre-dispatch clock status cannot finalize applied");
        staleStatus = Status() with { Context = Context() with { Identity = Identity() with { MapId = 1 } } };
        Throws(() => ledger.FinishClockUncertain(admission.Handle!, staleStatus, "wrong map"), "clock evidence cannot cross maps");
        var observed = Status();
        var receipt = ledger.FinishClockApplied(admission.Handle!, observed);
        Check(receipt.Applied.Status.Equals(observed) && receipt.AdmittedContext.Equals(Context()), "clock applied retains actual status and admitted context");
        var mutatedObserved = observed with { ActualPaused = false }; var mutatedReceipt = receipt with { AdmittedContext = receipt.AdmittedContext with { Tick = 99 } };
        Check(mutatedObserved.ActualPaused == false && mutatedReceipt.AdmittedContext.Tick == 99, "changed copies are separate values");
        var replay = ledger.InspectClock(Start, original);
        Check(replay.Kind == Kind.Replay && replay.Reply!.Receipt.Applied.Status.ActualPaused == true && replay.Reply.Receipt.AdmittedContext.Tick == 10, "clock result and receipt mutations cannot change replay");
        // (Decision replies were defensive protobuf clones; records are immutable, so there is nothing to mutate.)
        Check(replay.Reply!.Receipt.Attempt.AttemptId == 1, "clock decision replies keep the admitted attempt");
        var later = Context() with { Tick = 100, NativeGeneration = 999 };
        Check(ledger.LookupClock(original.Authority.Attempt, later).Receipt.Equals(replay.Reply!.Receipt), "clock receipt survives later generation changes");
        Throws(() => ledger.FinishClockUncertain(admission.Handle!, null, "late"), "clock terminal result is immutable");
        Throws(() => new NativeAttemptLedger(Identity()).FinishClockApplied(admission.Handle!, Status()), "clock foreign handle refused");
        Check(ledger.LookupClock(Pre(999).Attempt, Context()).Unknown != null, "never-admitted clock attempt remains unknown");
        later = Context() with { Identity = Identity() with { MapId = 1 } };
        Check(ledger.LookupClock(original.Authority.Attempt, later).Failure.Code == Common.FailureCode.StaleIdentity, "clock lookup cannot cross original map");
        later = Context() with { Identity = Identity() with { LoadToken = "new" } };
        Check(ledger.LookupClock(original.Authority.Attempt, later).Failure.Code == Common.FailureCode.StaleIdentity, "clock lookup cannot cross load");
        Check(new NativeAttemptLedger(later.Identity).LookupClock(original.Authority.Attempt, later).Unknown != null, "new load never retains old clock attempts");
        Exception threadError = null;
        var thread = new Thread(() => { try { ledger.LookupClock(original.Authority.Attempt, Context()); } catch (Exception error) { threadError = error; } });
        thread.Start(); thread.Join(); Check(threadError is InvalidOperationException, "clock ledger access remains main-thread affine");
        Equality(ledger, original);
        Variants(ledger);
        return checks;
    }
    private static void Equality(NativeAttemptLedger ledger, Clock.StartRequest original)
    {
        foreach (var change in new Func<Clock.StartRequest, Clock.StartRequest>[] {
            r => r with { Authority = r.Authority with { ExpectedGeneration = r.Authority.ExpectedGeneration + 1 } },
            r => r with { Authority = r.Authority with { Identity = r.Authority.Identity with { MapId = 1 } } },
            r => r with { Speed = Clock.Speed.Fast }, r => r with { LeaseMs = r.LeaseMs + 1 }, r => r with { MaxTicks = r.MaxTicks + 1 },
            r => r with { Policy = r.Policy with { HealthDropFraction = null } },
            r => r with { Policy = r.Policy with { AcknowledgedHostileIds = ["hostile"] } } })
        {
            var changed = change(original);
            Check(ledger.InspectClock(Start, changed).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "changed clock typed value or presence conflicts");
        }
        var json = RimGovernor.Contracts.Wire.WireJson.Deserialize<Clock.StartRequest>(RimGovernor.Contracts.Wire.WireJson.Serialize(original));
        Check(ledger.InspectClock(Start, json).Kind == Kind.Replay, "JSON roundtrip preserves clock retry identity");
        foreach (var change in new Func<Clock.StartRequest, Clock.StartRequest>[] {
            r => r with { Authority = r.Authority with { Attempt = r.Authority.Attempt with { AttemptId = 0 } } },
            r => r with { Authority = r.Authority with { Attempt = r.Authority.Attempt with { ActionId = null } } },
            r => r with { Authority = r.Authority with { Identity = r.Authority.Identity with { MapId = null } } },
            r => r with { Authority = r.Authority with { ExpectedGeneration = 0 } },
            r => r with { Authority = r.Authority with { Identity = r.Authority.Identity with { ColonyId = "\ud800" } } } })
        {
            var changed = change(original);
            Check(ledger.InspectClock(Start, changed).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "invalid clock envelope refused");
        }
        // Unknown fields: the protobuf probe appended unknown bytes to the request; the JSON decoder rejects unknown members
        // (UnmappedMemberHandling.Disallow), so an unknown root or nested member never reaches the ledger as a record.
        var originalJson = RimGovernor.Contracts.Wire.WireJson.Serialize(original);
        ThrowsJson(() => RimGovernor.Contracts.Wire.WireJson.Deserialize<Clock.StartRequest>("{\"unexpected\":1," + originalJson.Substring(1)), "clock unknown root member rejected");
        ThrowsJson(() => RimGovernor.Contracts.Wire.WireJson.Deserialize<Clock.StartRequest>(originalJson.Replace("\"policy\":{", "\"policy\":{\"unexpected\":1,")), "clock unknown nested member rejected");
        Check(ledger.InspectClock(Start.ToLowerInvariant(), original).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock full method spelling is fixed");
        Check(ledger.InspectClock(Renew, original).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "clock request cannot impersonate another method");
        Check(ledger.InspectClock(Start, new Clock.OwnedRequest()).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "unguarded pause is outside admitted request union");
        Check(ledger.InspectClock(Start, new Receipts.LookupRequest()).Reply!.Failure.Code == Common.FailureCode.InvalidRequest, "other messages cannot be smuggled as clock requests");
        var repeated = Request(10) with { Policy = Request(10).Policy with { AcknowledgedHostileIds = ["first", "second"] } };
        ledger.AdmitClock(Start, repeated, Context());
        repeated = repeated with { Policy = repeated.Policy with { AcknowledgedHostileIds = ["second", "first"] } };
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
        var changedRenew = renew with { Epoch = renew.Epoch with { Owner = renew.Epoch.Owner with { Epoch = renew.Epoch.Owner.Epoch + 1 } } };
        Check(ledger.InspectClock(Renew, changedRenew).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "original epoch ownership participates in retry equality");
        var speed = new Clock.SpeedRequest { Authority = Pre(4), Epoch = epoch, Speed = Clock.Speed.Superfast };
        admission = ledger.AdmitClock(Speed, speed, Context());
        Check(admission.Kind == Kind.Admitted, "typed speed change admitted");
        var evidence = Status();
        var receipt = ledger.FinishClockUncertain(admission.Handle!, evidence, "partially observed");
        var mutatedEvidence = evidence with { ActualPaused = false };
        Check(mutatedEvidence.ActualPaused == false && receipt.Uncertain.LastObserved.ActualPaused == true && ledger.LookupClock(speed.Authority.Attempt, Context()).Receipt.Equals(receipt), "uncertain speed status is detached and retained");
        var conflictingSpeed = speed with { Authority = renew.Authority };
        Check(ledger.InspectClock(Speed, conflictingSpeed).Reply!.Failure.Code == Common.FailureCode.AttemptConflict, "clock methods share attempt namespace");
        var max = Request(ulong.MaxValue);
        Check(ledger.AdmitClock(Start, max, Context()).Kind == Kind.Admitted, "clock uint64 maximum attempt is preserved");
    }
}
