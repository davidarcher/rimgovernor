#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private sealed class ContextStub : RimBridgeServer.Sdk.IRimBridgeContext, RimBridgeServer.Sdk.IMainThread
    {
        public Dictionary<string, object>? Arguments { get; set; } = new Dictionary<string, object>();
        public RimBridgeServer.Sdk.IMainThread MainThread => this;
        public int Invocations;
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Invocations++;
            return Task.FromResult(action());
        }
    }
    private static Wire.StatusReply Decode(object envelope)
    {
        var fields = (Dictionary<string, object>)envelope;
        Check(fields.Count == 1 && fields["payload"] is string, "Exact typed payload envelope");
        return Wire.StatusReply.Parser.ParseJson((string)fields["payload"]);
    }
    private static void EndpointAdmission()
    {
        var game = new Game(); Current.Game = game; Find.CurrentMap = new Map { uniqueID = 0 };
        Find.TickManager = new TickManager { TicksGame = 0 };
        var endpoint = new NativeAuthorityTools(); var ctx = new ContextStub();
        var missing = Decode(endpoint.ReadStatus(ctx, CancellationToken.None).GetAwaiter().GetResult());
        Check(missing.OutcomeCase == Wire.StatusReply.OutcomeOneofCase.Failure
            && missing.Failure.Code == Common.FailureCode.InvalidRequest, "Missing request reaches typed failure");
        Check(ctx.Invocations == 0, "Missing request entered game dispatcher");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Missing request allocated authority");
        ctx.Arguments!["request"] = 42;
        var nonString = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, 42).GetAwaiter().GetResult());
        Check(nonString.Failure.Code == Common.FailureCode.InvalidRequest && ctx.Invocations == 0, "Non-string request was admitted");
        var request = JsonFormatter.Default.Format(new Wire.StatusRequest { Identity = Context.Identity.Clone() });
        ctx.Arguments["request"] = request;
        ctx.Arguments["extra"] = true;
        Check(Decode(endpoint.ReadStatus(ctx, CancellationToken.None, request).GetAwaiter().GetResult()).Failure.Code
            == Common.FailureCode.InvalidRequest && ctx.Invocations == 0, "Unknown outer argument was admitted");
        ctx.Arguments.Remove("extra");
        for (int i = 0; i < 2; i++)
        {
            var absent = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, request).GetAwaiter().GetResult());
            Check(absent.Status.StateCase == Wire.Status.StateOneofCase.Unavailable
                && absent.Status.Unavailable.Reason == Common.UnavailableReason.NotObserved, "Missing state did not report unavailable");
            Check(!absent.Status.Context.HasNativeGeneration, "Missing state fabricated authority generation");
            Check(!NativeControlAuthority.TryGetForGame(game, out _), "Read allocated native authority");
        }
        Check(ctx.Invocations == 2, "Valid reads did not dispatch exactly once each");
        var staleRequest = JsonFormatter.Default.Format(new Wire.StatusRequest { Identity = new Common.Identity
            { ColonyId = "colony", LoadToken = "stale", MapId = 0 } });
        ctx.Arguments["request"] = staleRequest;
        var stale = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, staleRequest).GetAwaiter().GetResult());
        Check(stale.Failure.Code == Common.FailureCode.StaleIdentity, "Stale identity accepted");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Stale read allocated authority");
        var existing = NativeControlAuthority.ForGame(game);
        ctx.Arguments["request"] = request;
        var known = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, request).GetAwaiter().GetResult());
        Check(known.Status.Context.HasNativeGeneration && known.Status.Context.NativeGeneration == 1, "Existing generation omitted");
        Check(known.Status.StateCase == Wire.Status.StateOneofCase.Unavailable
            && !existing.Status().Available && !existing.Status().Active, "Read enabled unverified authority");
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
        EndpointAdmission();
        Console.WriteLine("Native authority status passed " + checks + " projection and endpoint-boundary assertions; SDK transport/native gameplay remain separate.");
    }
}
