using System;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Wire = RimGovernor.Protocol.Authority;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static readonly Common.ObservationContext Context = new Common.ObservationContext
    {
        Identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 },
        Tick = 0, NativeGeneration = 1
    };
    private static Wire.Status Project(NativeControlSnapshot snapshot)
    {
        var status = NativeAuthorityTools.Project(snapshot, Context);
        var json = JsonFormatter.Default.Format(status);
        Check(status.Equals(Wire.Status.Parser.ParseJson(json)), "Official ProtoJSON roundtrip");
        Check(status.Equals(Wire.Status.Parser.ParseFrom(status.ToByteArray())), "Official binary roundtrip");
        Check(Context.NativeGeneration == 1, "Projection mutated caller observation context");
        Check(status.Context.NativeGeneration == snapshot.Generation, "Projection lost refreshed generation");
        Check(status.Context.HasTick && status.Context.Tick == 0 && status.Context.Identity.HasMapId,
            "Projection lost known tick/map zero");
        return status;
    }
    private static void Main()
    {
        var game = new Game(); var map = new Map { uniqueID = 0 };
        var identity = new NativeControlIdentity(game, map, "colony", "load");
        long time = 0;
        var state = new NativeControlAuthority(game, () => identity, () => time);
        var initial = Project(state.Status());
        Check(initial.StateCase == Wire.Status.StateOneofCase.Unavailable, "Unverified hooks must report unavailable");
        Check(initial.Unavailable.Reason == Common.UnavailableReason.NotObserved, "Unverified hooks reason");
        Check(!state.Status().Active && !state.Status().Available, "Status mapping enabled authority");
        state.SetHookHealth(true);
        var acquired = state.Acquire(state.Status().Generation, "controller/α", ulong.MaxValue, 1000);
        Check(acquired.Success, "State fixture grant");
        var active = Project(acquired.Snapshot);
        Check(active.StateCase == Wire.Status.StateOneofCase.Active, "Active branch");
        Check(active.Active.Owner.PlayerDirection == ulong.MaxValue && active.Active.Owner.ControllerSessionId == "controller/α", "Exact owner/direction");
        Check(active.Active.RemainingLeaseMs == 1000, "Exact lease remaining");
        Check(!JsonFormatter.Default.Format(active).Contains(acquired.Snapshot.Lease!.LeaseId), "Read status disclosed lease secret");
        time = 1000;
        var expired = Project(state.Status());
        Check(expired.StateCase == Wire.Status.StateOneofCase.Inactive && expired.Inactive.Reason == Wire.RevocationReason.LeaseExpired,
            "Exact deadline maps inactive expiry");
        foreach (NativeControlRevocationReason reason in Enum.GetValues(typeof(NativeControlRevocationReason)))
        {
            if (reason == NativeControlRevocationReason.ClockUnavailable) continue;
            var wire = Project(new NativeControlSnapshot(identity, ulong.MaxValue, true, null, 0, reason));
            Check(wire.StateCase == Wire.Status.StateOneofCase.Inactive, "Known reason inactive: " + reason);
            Check(wire.Inactive.Reason.ToString() == reason.ToString(), "Explicit semantic reason mapping: " + reason);
        }
        var missing = Project(new NativeControlSnapshot(null, 5, false, null, 0, NativeControlRevocationReason.IdentityChanged));
        Check(missing.Unavailable.Reason == Common.UnavailableReason.NotLoaded, "Missing identity unavailable");
        var clock = Project(new NativeControlSnapshot(identity, 6, false, null, 0, NativeControlRevocationReason.ClockUnavailable));
        Check(clock.Unavailable.Reason == Common.UnavailableReason.ReadFailed, "Clock failure is unavailable, not invented wire enum");
        var exhausted = Project(new NativeControlSnapshot(identity, ulong.MaxValue, false, null, 0, NativeControlRevocationReason.GenerationExhausted));
        Check(exhausted.Unavailable.Reason == Common.UnavailableReason.LimitExceeded, "Generation exhaustion unavailable");
        var unknown = Project(new NativeControlSnapshot(identity, 7, true, null, 0, (NativeControlRevocationReason)999));
        Check(unknown.StateCase == Wire.Status.StateOneofCase.Unavailable, "Unknown internal enum leaked onto wire");
        Console.WriteLine("Native authority status projection passed " + checks + " assertions; SDK dispatch/gameplay not simulated.");
    }
}
