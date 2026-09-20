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

internal static class NativeAuthorityStatusProbe
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
        public string OperationId => "probe";
        public string CapabilityId => "probe";
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
        // The envelope carries the payload plus, from a main-thread hop, the
        // companion's own timing split (#81); nothing else.
        Check(fields["payload"] is string && fields.Count == (fields.ContainsKey(ProtoBoundary.TimingField) ? 2 : 1), "Exact typed payload envelope");
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
    // Orderly exit (#88): an unloaded game's final authority stays readable for
    // its own identity once no game is loaded, as Inactive(SHUTDOWN) at the
    // revoking generation; an already inactive authority keeps its reason, and
    // any other identity still fails as before.
    private static void Shutdown()
    {
        var game = new Game(); Current.Game = game; Find.CurrentMap = new Map { uniqueID = 0 };
        Find.TickManager = new TickManager { TicksGame = 0 };
        var identity = new NativeControlIdentity(game, Find.CurrentMap, "colony", "load");
        var state = new NativeControlAuthority(game, () => Current.Game == game ? identity : null, () => 0);
        state.SetHookHealth(true);
        Check(state.SetMode(1, NativeControlMode.Auto).Success, "Shutdown fixture grant");
        var revoked = state.RevokeShutdown(42);
        Check(!revoked.Active && revoked.Generation == 3 && revoked.Reason == NativeControlRevocationReason.Shutdown,
            "Active authority revoked as Shutdown at the next generation");
        var retained = NativeControlAuthority.LastShutdown;
        Check(retained != null && retained.Matches("colony", "load", 0) && retained.Tick == 42 && retained.Generation == 3
            && retained.Reason == NativeControlRevocationReason.Shutdown, "Final state retained for the unloaded identity");
        Check(state.RevokeShutdown(43).Generation == 3 && NativeControlAuthority.LastShutdown!.Tick == 43,
            "A second shutdown neither advances the generation nor loses the reason");

        Current.Game = null; Find.CurrentMap = null; Find.TickManager = null;
        var endpoint = new NativeAuthorityTools(); var ctx = new ContextStub();
        var request = JsonFormatter.Default.Format(new Wire.StatusRequest { Identity = Context.Identity.Clone() });
        ctx.Arguments!["request"] = request;
        var read = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, request).GetAwaiter().GetResult());
        Check(read.OutcomeCase == Wire.StatusReply.OutcomeOneofCase.Status && read.Status.StateCase == Wire.Status.StateOneofCase.Inactive
            && read.Status.Inactive.Reason == Wire.RevocationReason.Shutdown, "Unloaded game reads Inactive(SHUTDOWN)");
        Check(read.Status.Context.Identity.Equals(Context.Identity) && read.Status.Context.HasTick && read.Status.Context.Tick == 43
            && read.Status.Context.NativeGeneration == 3, "Retained context carries the final identity, tick and generation");
        var other = JsonFormatter.Default.Format(new Wire.StatusRequest { Identity = new Common.Identity
            { ColonyId = "colony", LoadToken = "other", MapId = 0 } });
        ctx.Arguments["request"] = other;
        var unknown = Decode(endpoint.ReadStatus(ctx, CancellationToken.None, other).GetAwaiter().GetResult());
        Check(unknown.OutcomeCase == Wire.StatusReply.OutcomeOneofCase.Failure && unknown.Failure.Code == Common.FailureCode.Unavailable,
            "Another identity still fails with no game loaded");

        // Manual before the unload: the retained reason is Manual, not Shutdown.
        var next = new Game(); Current.Game = next; Find.CurrentMap = new Map { uniqueID = 1 };
        Find.TickManager = new TickManager { TicksGame = 7 };
        var nextIdentity = new NativeControlIdentity(next, Find.CurrentMap, "colony", "reload");
        var manual = new NativeControlAuthority(next, () => Current.Game == next ? nextIdentity : null, () => 0);
        manual.SetHookHealth(true);
        Check(manual.SetMode(1, NativeControlMode.Auto).Success && manual.SetMode(2, NativeControlMode.Manual).Success, "Manual fixture");
        Check(manual.RevokeShutdown(7).Generation == 3 && NativeControlAuthority.LastShutdown!.Reason == NativeControlRevocationReason.Manual
            && NativeControlAuthority.LastShutdown.Matches("colony", "reload", 1), "Inactive authority keeps its reason through unload");
        Current.Game = null; Find.CurrentMap = null; Find.TickManager = null;
    }

    internal static void Invoke()
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
        var granted = state.SetMode(state.Status().Generation, NativeControlMode.Auto);
        Check(granted.Success, "State fixture grant");
        var active = Project(granted.Snapshot);
        Check(active.StateCase == Wire.Status.StateOneofCase.Active, "Active branch");
        Check(active.Active.HasMode && active.Active.Mode == Wire.Mode.Auto, "Active authority is the bot's Auto mode");
        Check(active.Context.NativeGeneration == 2, "Active projection carries the granted generation");
        time = 1000;
        var still = Project(state.Status());
        Check(still.StateCase == Wire.Status.StateOneofCase.Active && still.Context.NativeGeneration == 2, "Time alone must not expire Auto mode");
        var manual = state.SetMode(2, NativeControlMode.Manual);
        var inactive = Project(manual.Snapshot);
        Check(inactive.StateCase == Wire.Status.StateOneofCase.Inactive && inactive.Inactive.Reason == Wire.RevocationReason.Manual
            && inactive.Context.NativeGeneration == 3, "Manual mode maps to inactive with its generation");
        foreach (NativeControlRevocationReason reason in Enum.GetValues(typeof(NativeControlRevocationReason)))
        {
            if (reason == NativeControlRevocationReason.ClockUnavailable) continue;
            var wire = Project(new NativeControlSnapshot(identity, ulong.MaxValue, true, false, reason, null));
            Check(wire.StateCase == Wire.Status.StateOneofCase.Inactive, "Known reason inactive: " + reason);
            Check(wire.Inactive.Reason.ToString() == reason.ToString(), "Explicit semantic reason mapping: " + reason);
        }
        var missing = Project(new NativeControlSnapshot(null, 5, false, false, NativeControlRevocationReason.IdentityChanged, null));
        Check(missing.Unavailable.Reason == Common.UnavailableReason.NotLoaded, "Missing identity unavailable");
        var clock = Project(new NativeControlSnapshot(identity, 6, false, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clock.Unavailable.Reason == Common.UnavailableReason.ReadFailed, "Clock failure is unavailable, not invented wire enum");
        var clockReason = Project(new NativeControlSnapshot(identity, 6, true, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clockReason.StateCase == Wire.Status.StateOneofCase.Inactive && clockReason.Inactive.Reason == Wire.RevocationReason.Unavailable,
            "Clock revocation maps to the wire's terminal Unavailable reason");
        var exhausted = Project(new NativeControlSnapshot(identity, ulong.MaxValue, false, false, NativeControlRevocationReason.GenerationExhausted, null));
        Check(exhausted.Unavailable.Reason == Common.UnavailableReason.LimitExceeded, "Generation exhaustion unavailable");
        var unknown = Project(new NativeControlSnapshot(identity, 7, true, false, (NativeControlRevocationReason)999, null));
        Check(unknown.StateCase == Wire.Status.StateOneofCase.Unavailable, "Unknown internal enum leaked onto wire");
        EndpointAdmission();
        Shutdown();
        Console.WriteLine("Native authority status passed " + checks + " projection and endpoint-boundary assertions; SDK transport/native gameplay remain separate.");
    }
}
