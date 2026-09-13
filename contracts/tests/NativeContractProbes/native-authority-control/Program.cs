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
        public int Invocations;
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Invocations++; return Task.FromResult(action()); }
    }
    private static Wire.ControlReply Decode(object value)
    {
        var envelope = (Dictionary<string, object>)value;
        Check(envelope.Count == 1 && envelope["payload"] is string, "payload envelope");
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
    private static Wire.ControlRequest Acquire(ulong? generation = null) => new() { Acquire = new Wire.Acquire
    {
        Identity = Identity.Clone(), ExpectedGeneration = generation ?? state.Status().Generation,
        Owner = new Wire.Owner { ControllerSessionId = "controller/α", PlayerDirection = ulong.MaxValue }, LeaseMs = 1000
    } };
    private static Wire.ControlRequest Renew(Wire.Granted grant) => new() { Renew = new Wire.Renew
    {
        Identity = Identity.Clone(), ExpectedGeneration = grant.Context.NativeGeneration,
        ControllerSessionId = "controller/α", LeaseId = grant.LeaseId, LeaseMs = 30000
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
        foreach (var json in new[] { "null", "{", "{\"unknown\":true}", "{\"acquire\":{\"expectedGeneration\":\"-1\"}}" })
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
        foreach (Action<Wire.Acquire> mutate in new Action<Wire.Acquire>[] {
            a => a.ClearExpectedGeneration(), a => a.ExpectedGeneration = 0,
            a => a.Owner = null, a => a.Owner.ClearControllerSessionId(), a => a.Owner.ControllerSessionId = "",
            a => a.Owner.ControllerSessionId = " ", a => a.Owner.ControllerSessionId = "bad\0id",
            a => a.Owner.ControllerSessionId = new string('é', 129),
            a => a.Owner.ClearPlayerDirection(), a => a.Owner.PlayerDirection = 0,
            a => a.ClearLeaseMs(), a => a.LeaseMs = 999, a => a.LeaseMs = 30001, a => a.LeaseMs = uint.MaxValue,
            a => a.Identity = null, a => a.Identity.ClearMapId() })
        {
            var request = Acquire(); mutate(request.Acquire); Failure(Call(request), Common.FailureCode.InvalidRequest);
            Check(state.Status().Generation == generation && !state.Status().Active, "invalid acquire mutated state");
        }
        var stale = Acquire(); stale.Acquire.Identity.LoadToken = "old";
        Failure(Call(stale), Common.FailureCode.StaleIdentity);
        Failure(Call(Acquire(generation + 1)), Common.FailureCode.StaleGeneration);
    }
    private static void EagerInitialization()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        NativeAuthorityHooks.Ready = true;
        var initializations = NativeAuthorityHooks.Initializations;
        var request = Acquire(1);
        request.Acquire.Identity.LoadToken = "stale";
        Failure(Call(request), Common.FailureCode.StaleIdentity);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "stale acquire initialized hooks");
        request.Acquire.Identity = Identity.Clone();
        var reply = Call(request);
        Check(reply.Granted != null && NativeAuthorityHooks.Initializations == initializations + 1, "validated acquire did not eagerly initialize hooks");
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out var existing) && existing!.Status().Active, "eager acquire missing registered authority");
        Current.Game = new Game(); NativeAuthorityHooks.Ready = false;
        Failure(Call(request), Common.FailureCode.Unavailable);
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out existing) && !existing!.Status().Active && !existing.Status().Available, "unverified eager acquire granted authority");
    }
    private static void GrantsAndRevocations()
    {
        Reset(); var before = state.Status().Generation; var granted = Call(Acquire());
        Check(granted.Granted != null && granted.Granted.Context.NativeGeneration == before + 1, "acquire exact increment");
        var grant = granted.Granted!;
        Check(grant.Authority.Owner.PlayerDirection == ulong.MaxValue && grant.Authority.Owner.ControllerSessionId == "controller/α"
            && grant.LeaseId.Length > 0 && grant.Authority.RemainingLeaseMs == 1000, "grant retained owner/lease/direction/duration");
        Failure(Call(Acquire()), Common.FailureCode.OwnerConflict);
        var renewed = Call(Renew(grant));
        Check(renewed.Granted.Context.NativeGeneration == grant.Context.NativeGeneration && renewed.Granted.LeaseId == grant.LeaseId
            && renewed.Granted.Authority.RemainingLeaseMs == 30000, "renew changed generation or lease");
        var generationBeforeInvalid = state.Status().Generation;
        foreach (Action<Wire.Renew> mutate in new Action<Wire.Renew>[] {
            r => r.ClearExpectedGeneration(), r => r.ExpectedGeneration = 0,
            r => r.ClearControllerSessionId(), r => r.ControllerSessionId = " ",
            r => r.ClearLeaseId(), r => r.LeaseId = "", r => r.LeaseId = "bad\0id",
            r => r.ClearLeaseMs(), r => r.LeaseMs = 0, r => r.LeaseMs = 30001,
            r => r.Identity = null })
        {
            var invalid = Renew(grant); mutate(invalid.Renew);
            Failure(Call(invalid), Common.FailureCode.InvalidRequest);
            Check(state.Status().Active && state.Status().Generation == generationBeforeInvalid, "invalid renewal mutated authority");
        }
        var wrong = Renew(grant); wrong.Renew.ControllerSessionId = "other"; Failure(Call(wrong), Common.FailureCode.OwnerConflict);
        wrong = Renew(grant); wrong.Renew.LeaseId = "wrong"; Failure(Call(wrong), Common.FailureCode.AuthorityRequired);
        wrong = Renew(grant); wrong.Renew.ExpectedGeneration++; Failure(Call(wrong), Common.FailureCode.StaleGeneration);
        foreach (var reason in new[] { Wire.RevocationReason.Manual, Wire.RevocationReason.PlayerDirection, Wire.RevocationReason.Disconnect, Wire.RevocationReason.Shutdown })
        {
            if (!state.Status().Active) grant = Call(Acquire()).Granted;
            var generation = state.Status().Generation;
            var reply = Call(new Wire.ControlRequest { Revoke = new Wire.Revoke { Identity = Identity.Clone(), ExpectedGeneration = generation, Reason = reason } });
            Check(reply.Revoked != null && reply.Revoked.Context.NativeGeneration == generation + 1 && reply.Revoked.Authority.Reason == reason, "revoke exact generation/reason");
            Failure(Call(Renew(grant)), Common.FailureCode.StaleGeneration);
            Check(!state.Status().Active, "renew reacquired after revoke");
        }
        foreach (var reason in new[] { Wire.RevocationReason.Unspecified, Wire.RevocationReason.LeaseExpired, (Wire.RevocationReason)999 })
            Failure(Call(new Wire.ControlRequest { Revoke = new Wire.Revoke { Identity = Identity.Clone(), ExpectedGeneration = state.Status().Generation, Reason = reason } }), Common.FailureCode.InvalidRequest);
        foreach (Action<Wire.Revoke> mutate in new Action<Wire.Revoke>[] {
            r => r.ClearExpectedGeneration(), r => r.ExpectedGeneration = 0, r => r.ClearReason(), r => r.Identity = null })
        {
            var invalid = new Wire.Revoke { Identity = Identity.Clone(), ExpectedGeneration = state.Status().Generation, Reason = Wire.RevocationReason.Manual };
            mutate(invalid); Failure(Call(new Wire.ControlRequest { Revoke = invalid }), Common.FailureCode.InvalidRequest);
        }
        Reset(); grant = Call(Acquire()).Granted; time = 1000;
        Failure(Call(Renew(grant)), Common.FailureCode.StaleGeneration);
        Check(!state.Status().Active && state.Status().Reason == NativeControlRevocationReason.LeaseExpired, "expired renew restored authority");
        NativeAuthorityHooks.Ready = false; state.SetHookHealth(false);
        Failure(Call(Acquire()), Common.FailureCode.Unavailable);
        Check(!state.Status().Active, "unverified hooks granted");
    }
    internal static void Invoke()
    {
        Boundary(); InvalidFields(); EagerInitialization(); GrantsAndRevocations();
        Console.WriteLine($"Native authority Control: {checks} checks (production parser, identity, adapter and state; SDK/hook transport seams).");
    }
}
