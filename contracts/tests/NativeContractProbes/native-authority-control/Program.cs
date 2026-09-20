#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Wire = RimGovernor.Protocol.Authority;

// Drives the production authority_control adapter end to end (parser, identity,
// hook initialization, state). Control is SetMode(Auto|Manual) or Revoke under
// an exact generation (#52): Auto grants outright, Manual/Revoke revoke, and
// every accepted operation advances the generation by exactly one.
internal static class NativeAuthorityControlProbe
{
    private static int checks;
    private static long time;
    private static NativeControlAuthority state = null!;
    private static readonly Common.Identity Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static void Check(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    private sealed class Context : RimBridgeServer.Sdk.IRimBridgeContext, RimBridgeServer.Sdk.IMainThread
    {
        public Dictionary<string, object>? Arguments { get; set; } = new();
        public RimBridgeServer.Sdk.IMainThread MainThread => this;
        public string OperationId => "probe";
        public string CapabilityId => "probe";
        public int Invocations;
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Invocations++; return Task.FromResult(action()); }
    }
    private static Wire.ControlReply Decode(object value)
    {
        var envelope = (Dictionary<string, object>)value;
        // payload plus, from a main-thread hop, the companion's timing split (#81); nothing else.
        Check(envelope["payload"] is string && envelope.Count == (envelope.ContainsKey(ProtoBoundary.TimingField) ? 2 : 1), "payload envelope");
        var reply = Wire.ControlReply.Parser.ParseJson((string)envelope["payload"]);
        Check(reply.Equals(Wire.ControlReply.Parser.ParseFrom(reply.ToByteArray())), "binary preserves reply");
        return reply;
    }
    private static Wire.ControlReply Call(Wire.ControlRequest request)
    {
        var json = JsonFormatter.Default.Format(request);
        var ctx = new Context(); ctx.Arguments!["request"] = json;
        var reply = Decode(new NativeAuthorityControlTools().Control(ctx, CancellationToken.None, json).GetAwaiter().GetResult());
        Check(ctx.Invocations == 1, "valid JSON must dispatch once");
        return reply;
    }
    private static Wire.ControlRequest SetMode(Wire.Mode mode, ulong? generation = null) => new() { SetMode = new Wire.SetMode
    {
        Identity = Identity.Clone(), ExpectedGeneration = generation ?? state.Status().Generation, Mode = mode
    } };
    private static Wire.ControlRequest Revoke(Wire.RevocationReason reason, ulong? generation = null) => new() { Revoke = new Wire.Revoke
    {
        Identity = Identity.Clone(), ExpectedGeneration = generation ?? state.Status().Generation, Reason = reason
    } };
    private static void Failure(Wire.ControlReply reply, Common.FailureCode code)
    { Check(reply.OutcomeCase == Wire.ControlReply.OutcomeOneofCase.Failure && reply.Failure.Code == code, "expected " + code + ": " + reply); }
    private static void Reset()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map { uniqueID = 0 }; Find.TickManager = new TickManager();
        state = NativeControlAuthority.ForGame(Current.Game); time = 0;
        // Replace only the monotonic clock on the registered production state; no waits or GC.
        typeof(NativeControlAuthority).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, (Func<long>)(() => time));
        NativeAuthorityHooks.Ready = true; state.SetHookHealth(true);
    }
    private static void Boundary()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        var ctx = new Context(); var endpoint = new NativeAuthorityControlTools();
        Failure(Decode(endpoint.Control(ctx, CancellationToken.None).GetAwaiter().GetResult()), Common.FailureCode.InvalidRequest);
        ctx.Arguments!["request"] = 5;
        Failure(Decode(endpoint.Control(ctx, CancellationToken.None, 5).GetAwaiter().GetResult()), Common.FailureCode.InvalidRequest);
        foreach (var json in new[] { "null", "{", "{\"unknown\":true}", "{\"setMode\":{\"expectedGeneration\":\"-1\"}}",
            "{\"acquire\":{\"expectedGeneration\":\"1\"}}", "{\"renew\":{\"expectedGeneration\":\"1\"}}" })
        {
            ctx.Arguments["request"] = json;
            Failure(Decode(endpoint.Control(ctx, CancellationToken.None, json).GetAwaiter().GetResult()), Common.FailureCode.InvalidRequest);
        }
        ctx.Arguments["request"] = "{}"; ctx.Arguments["extra"] = false;
        Failure(Decode(endpoint.Control(ctx, CancellationToken.None, "{}").GetAwaiter().GetResult()), Common.FailureCode.InvalidRequest);
        Check(ctx.Invocations == 0, "invalid boundary invoked game thread");
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "malformed control allocated state");
        Failure(Call(new Wire.ControlRequest()), Common.FailureCode.InvalidRequest);
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "missing operation allocated state");
    }
    private static void InvalidFields()
    {
        Reset(); var generation = state.Status().Generation;
        foreach (Action<Wire.SetMode> mutate in new Action<Wire.SetMode>[] {
            s => s.ClearExpectedGeneration(), s => s.ExpectedGeneration = 0,
            s => s.ClearMode(), s => s.Mode = Wire.Mode.Unspecified, s => s.Mode = (Wire.Mode)999,
            s => s.Identity = null, s => s.Identity.ClearMapId() })
        {
            var request = SetMode(Wire.Mode.Auto); mutate(request.SetMode); Failure(Call(request), Common.FailureCode.InvalidRequest);
            Check(state.Status().Generation == generation && !state.Status().Active, "invalid set_mode mutated state");
        }
        var stale = SetMode(Wire.Mode.Auto); stale.SetMode.Identity.LoadToken = "old";
        Failure(Call(stale), Common.FailureCode.StaleIdentity);
        Failure(Call(SetMode(Wire.Mode.Auto, generation + 1)), Common.FailureCode.StaleGeneration);
        Check(state.Status().Generation == generation && !state.Status().Active, "refused set_mode mutated state");
    }
    private static void EagerInitialization()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        NativeAuthorityHooks.Ready = true;
        var initializations = NativeAuthorityHooks.Initializations;
        var request = SetMode(Wire.Mode.Auto, 1);
        request.SetMode.Identity.LoadToken = "stale";
        Failure(Call(request), Common.FailureCode.StaleIdentity);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "stale set_mode initialized hooks");
        request.SetMode.Identity = Identity.Clone();
        // Manual before any Auto must not initialize hooks or allocate state: it has nothing to revoke.
        Failure(Call(SetMode(Wire.Mode.Manual, 1)), Common.FailureCode.Unavailable);
        Failure(Call(Revoke(Wire.RevocationReason.Manual, 1)), Common.FailureCode.Unavailable);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "Manual/Revoke initialized hooks");
        var reply = Call(request);
        Check(reply.Granted != null && NativeAuthorityHooks.Initializations == initializations + 1, "validated Auto did not eagerly initialize hooks");
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out var existing) && existing!.Status().Active, "eager Auto missing registered authority");
        Current.Game = new Game(); NativeAuthorityHooks.Ready = false;
        Failure(Call(request), Common.FailureCode.Unavailable);
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out existing) && !existing!.Status().Active && !existing.Status().Available, "unverified eager Auto granted authority");
    }
    private static void GrantsAndRevocations()
    {
        Reset(); var before = state.Status().Generation; var granted = Call(SetMode(Wire.Mode.Auto));
        Check(granted.Granted != null && granted.Granted.Context.NativeGeneration == before + 1, "Auto exact increment");
        var grant = granted.Granted!;
        Check(grant.Authority.HasMode && grant.Authority.Mode == Wire.Mode.Auto, "grant reports Auto mode");
        Check(state.Status().Active && state.Status().Generation == before + 1, "grant did not activate state");
        // Repeating Auto is not a conflict: the single bot re-advances its own generation.
        var again = Call(SetMode(Wire.Mode.Auto));
        Check(again.Granted != null && again.Granted.Context.NativeGeneration == before + 2, "repeated Auto must advance");
        Failure(Call(SetMode(Wire.Mode.Auto, before + 1)), Common.FailureCode.StaleGeneration);
        Check(state.Status().Active && state.Status().Generation == before + 2, "stale Auto mutated authority");
        time = 1000;
        Check(state.Status().Active && state.Status().Generation == before + 2, "time alone revoked: there is no lease");
        var manual = Call(SetMode(Wire.Mode.Manual));
        Check(manual.Revoked != null && manual.Revoked.Context.NativeGeneration == before + 3
            && manual.Revoked.Authority.Reason == Wire.RevocationReason.Manual, "Manual mode exact generation/reason");
        Check(!state.Status().Active, "Manual mode left authority active");
        var manualAgain = Call(SetMode(Wire.Mode.Manual));
        Check(manualAgain.Revoked != null && manualAgain.Revoked.Context.NativeGeneration == before + 4, "repeated Manual must still advance");
        foreach (var reason in new[] { Wire.RevocationReason.Manual, Wire.RevocationReason.Disconnect, Wire.RevocationReason.Shutdown })
        {
            if (!state.Status().Active) Check(Call(SetMode(Wire.Mode.Auto)).Granted != null, "re-grant before revoke");
            var generation = state.Status().Generation;
            var reply = Call(Revoke(reason));
            Check(reply.Revoked != null && reply.Revoked.Context.NativeGeneration == generation + 1 && reply.Revoked.Authority.Reason == reason, "revoke exact generation/reason");
            Failure(Call(SetMode(Wire.Mode.Auto, generation)), Common.FailureCode.StaleGeneration);
            Check(!state.Status().Active, "stale Auto reacquired after revoke");
        }
        // Wire reasons the host may not assert itself: player detection, identity, hooks, exhaustion, terminal unavailability.
        foreach (var reason in new[] { Wire.RevocationReason.Unspecified, Wire.RevocationReason.None, Wire.RevocationReason.PlayerControl,
            Wire.RevocationReason.ExternalOrder, Wire.RevocationReason.IdentityChanged, Wire.RevocationReason.HooksUnavailable,
            Wire.RevocationReason.GenerationExhausted, Wire.RevocationReason.Unavailable, (Wire.RevocationReason)999 })
            Failure(Call(Revoke(reason)), Common.FailureCode.InvalidRequest);
        foreach (Action<Wire.Revoke> mutate in new Action<Wire.Revoke>[] {
            r => r.ClearExpectedGeneration(), r => r.ExpectedGeneration = 0, r => r.ClearReason(), r => r.Identity = null })
        {
            var invalid = Revoke(Wire.RevocationReason.Manual); mutate(invalid.Revoke);
            Failure(Call(invalid), Common.FailureCode.InvalidRequest);
        }
        Failure(Call(Revoke(Wire.RevocationReason.Manual, state.Status().Generation + 1)), Common.FailureCode.StaleGeneration);
        Reset(); Check(Call(SetMode(Wire.Mode.Auto)).Granted != null, "grant");
        NativeAuthorityHooks.Ready = false; state.SetHookHealth(false);
        Check(!state.Status().Active && state.Status().Reason == NativeControlRevocationReason.HooksUnavailable, "hook loss did not revoke");
        Failure(Call(SetMode(Wire.Mode.Auto)), Common.FailureCode.Unavailable);
        Check(!state.Status().Active, "unverified hooks granted");
        Reset(); Check(Call(SetMode(Wire.Mode.Auto)).Granted != null, "grant");
        time = -1;
        Failure(Call(SetMode(Wire.Mode.Auto)), Common.FailureCode.Unavailable);
        Check(!state.Status().Active && !state.Status().Available, "clock fault granted");
    }
    internal static void Invoke()
    {
        Boundary(); InvalidFields(); EagerInitialization(); GrantsAndRevocations();
        Console.WriteLine($"Native authority Control: {checks} checks (production parser, identity, adapter and state; SDK/hook transport seams).");
    }
}
