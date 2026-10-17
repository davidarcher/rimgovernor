#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using HomeBridge.BridgeTools;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Wire = RimGovernor.Contracts.Wire.Authority;

// Drives the production authority_control adapter end to end (parser, identity,
// hook initialization, state). Control is SetMode(Auto|Manual) or Revoke under
// an exact generation: Auto grants outright, Manual/Revoke revoke, and
// every accepted operation advances the generation by exactly one.
internal static class NativeAuthorityControlProbe
{
    private static int checks;
    private static long time;
    private static NativeControlAuthority state = null!;
    private static readonly Common.Identity Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static void Check(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    // The /native/{op} route decodes the request with WireJson before the core runs, so a
    // request goes through JSON exactly as it would over the route.
    private static Wire.ControlReply Call(Wire.ControlRequest request)
    {
        var decoded = RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.ControlRequest>(RimGovernor.Contracts.Wire.WireJson.Serialize(request));
        var reply = NativeAuthorityControlTools.Apply(decoded);
        Check(reply.Equals(RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.ControlReply>(RimGovernor.Contracts.Wire.WireJson.Serialize(reply))), "JSON preserves reply");
        return reply;
    }
    // A wire enum value outside the schema cannot be held by a record (the encoder refuses it), so it is sent as raw JSON text.
    private static string UndefinedEnum(Wire.ControlRequest request, string member) =>
        System.Text.RegularExpressions.Regex.Replace(RimGovernor.Contracts.Wire.WireJson.Serialize(request), "\"" + member + "\":\"[^\"]*\"", "\"" + member + "\":\"UNDEFINED_999\"");
    // JSON the wire records refuse never reaches the core: the route answers 400 invalid_request.
    private static bool Undecodable(string json)
    {
        try { RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.ControlRequest>(json); return false; }
        catch (System.Text.Json.JsonException) { return true; }
    }
    private static Wire.ControlRequest SetMode(Wire.Mode mode, ulong? generation = null) => new() { Operation = new Wire.SetMode
    {
        Identity = Identity, ExpectedGeneration = generation ?? state.Status().Generation, Mode = mode
    } };
    private static Wire.ControlRequest Revoke(Wire.RevocationReason reason, ulong? generation = null) => new() { Operation = new Wire.Revoke
    {
        Identity = Identity, ExpectedGeneration = generation ?? state.Status().Generation, Reason = reason
    } };
    // Requests are immutable records: a change builds a modified copy of the operation.
    private static Wire.ControlRequest WithSetMode(Wire.ControlRequest request, Func<Wire.SetMode, Wire.SetMode> change) => new() { Operation = change(request.SetMode) };
    private static Wire.ControlRequest WithRevoke(Wire.ControlRequest request, Func<Wire.Revoke, Wire.Revoke> change) => new() { Operation = change(request.Revoke) };
    private static void Failure(Wire.ControlReply reply, Common.FailureCode code)
    { Check(reply.Failure != null && reply.Failure.Code == code, "expected " + code + ": " + reply); }
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
        foreach (var json in new[] { "null", "{", "{\"unknown\":true}", "{\"setMode\":{\"expectedGeneration\":\"-1\"}}",
            "{\"acquire\":{\"expectedGeneration\":\"1\"}}", "{\"renew\":{\"expectedGeneration\":\"1\"}}" })
            Check(Undecodable(json) || NativeAuthorityControlTools.Apply(RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.ControlRequest>(json) ?? new Wire.ControlRequest()).Failure?.Code == Common.FailureCode.InvalidRequest,
                "malformed control refused: " + json);
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "malformed control allocated state");
        Failure(Call(new Wire.ControlRequest()), Common.FailureCode.InvalidRequest);
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "missing operation allocated state");
    }
    private static void InvalidFields()
    {
        Reset(); var generation = state.Status().Generation;
        foreach (Func<Wire.SetMode, Wire.SetMode> mutate in new Func<Wire.SetMode, Wire.SetMode>[] {
            s => s with { ExpectedGeneration = null }, s => s with { ExpectedGeneration = 0 },
            s => s with { Mode = null }, s => s with { Mode = Wire.Mode.Unspecified },
            s => s with { Identity = null } })
        {
            var request = WithSetMode(SetMode(Wire.Mode.Auto), mutate); Failure(Call(request), Common.FailureCode.InvalidRequest);
            Check(state.Status().Generation == generation && !state.Status().Active, "invalid set_mode mutated state");
        }
        Check(Undecodable(UndefinedEnum(SetMode(Wire.Mode.Auto), "mode")), "undefined set_mode mode refused before dispatch");
        Check(state.Status().Generation == generation && !state.Status().Active, "undefined set_mode mode mutated state");
        var stale = WithSetMode(SetMode(Wire.Mode.Auto), s => s with { Identity = s.Identity with { LoadToken = "old" } });
        Failure(Call(stale), Common.FailureCode.StaleIdentity);
        Failure(Call(SetMode(Wire.Mode.Auto, generation + 1)), Common.FailureCode.StaleGeneration);
        Check(state.Status().Generation == generation && !state.Status().Active, "refused set_mode mutated state");
    }
    private static void EagerInitialization()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        NativeAuthorityHooks.Ready = true;
        var initializations = NativeAuthorityHooks.Initializations;
        var request = WithSetMode(SetMode(Wire.Mode.Auto, 1), s => s with { Identity = s.Identity with { LoadToken = "stale" } });
        Failure(Call(request), Common.FailureCode.StaleIdentity);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "stale set_mode initialized hooks");
        request = SetMode(Wire.Mode.Auto, 1);
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
        Check(grant.Authority.Mode == Wire.Mode.Auto, "grant reports Auto mode");
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
            Wire.RevocationReason.GenerationExhausted, Wire.RevocationReason.Unavailable })
            Failure(Call(Revoke(reason)), Common.FailureCode.InvalidRequest);
        Check(Undecodable(UndefinedEnum(Revoke(Wire.RevocationReason.Manual), "reason")), "undefined revoke reason refused before dispatch");
        foreach (Func<Wire.Revoke, Wire.Revoke> mutate in new Func<Wire.Revoke, Wire.Revoke>[] {
            r => r with { ExpectedGeneration = null }, r => r with { ExpectedGeneration = 0 }, r => r with { Reason = null }, r => r with { Identity = null } })
        {
            var invalid = WithRevoke(Revoke(Wire.RevocationReason.Manual), mutate);
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
