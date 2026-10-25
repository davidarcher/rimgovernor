using System;
using System.Collections.Generic;
using System.Reflection;
using RimGovernor.Native;
using Verse;

// Drives the production authority_control adapter end to end (parser, identity,
// hook initialization, state). Control is SetMode(Auto|Manual) or Revoke under
// an exact generation: Auto grants outright, Manual/Revoke revoke, and
// every accepted operation advances the generation by exactly one.
internal static class NativeAuthorityControlProbe
{
    private static int checks;
    private static long time;
    private static NativeControlAuthority? state;
    private static NativeControlAuthority State => state ?? throw new Exception("authority fixture has not been reset");
    private static readonly RimGovernor.Brain.GameFacts.Common.Identity Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    // The /native/{op} route decodes the request with OrdersJson before the core runs, so a
    // request goes through JSON exactly as it would over the route.
    private static RimGovernor.Brain.GameFacts.Authority.ControlReply Call(RimGovernor.Brain.GameOrders.Authority.ControlRequest request)
    {
        var decoded = RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Authority.ControlRequest>(RimGovernor.Brain.GameOrders.OrdersJson.Serialize(request));
        var reply = NativeAuthorityControlTools.Apply(decoded);
        Check(reply.Equals(RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Authority.ControlReply>(RimGovernor.Brain.GameOrders.OrdersJson.Serialize(reply))), "JSON preserves reply");
        return reply;
    }
    // A wire enum value outside the schema cannot be held by a record (the encoder refuses it), so it is sent as raw JSON text.
    private static string UndefinedEnum(RimGovernor.Brain.GameOrders.Authority.ControlRequest request, string member) =>
        System.Text.RegularExpressions.Regex.Replace(RimGovernor.Brain.GameOrders.OrdersJson.Serialize(request), "\"" + member + "\":\"[^\"]*\"", "\"" + member + "\":\"UNDEFINED_999\"");
    // JSON the wire records refuse never reaches the core: the route answers 400 invalid_request.
    private static bool Undecodable(string json)
    {
        try { RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Authority.ControlRequest>(json); return false; }
        catch (System.Text.Json.JsonException) { return true; }
    }
    private static RimGovernor.Brain.GameOrders.Authority.ControlRequest SetMode(RimGovernor.Brain.GameFacts.Authority.Mode mode, ulong? generation = null) => new() { Operation = new RimGovernor.Brain.GameOrders.Authority.SetMode
    {
        Identity = Identity, ExpectedGeneration = generation ?? State.Status().Generation, Mode = mode
    } };
    private static RimGovernor.Brain.GameOrders.Authority.ControlRequest Revoke(RimGovernor.Brain.GameFacts.Authority.RevocationReason reason, ulong? generation = null) => new() { Operation = new RimGovernor.Brain.GameOrders.Authority.Revoke
    {
        Identity = Identity, ExpectedGeneration = generation ?? State.Status().Generation, Reason = reason
    } };
    // Requests are immutable records: a change builds a modified copy of the operation.
    private static RimGovernor.Brain.GameOrders.Authority.ControlRequest WithSetMode(RimGovernor.Brain.GameOrders.Authority.ControlRequest request, Func<RimGovernor.Brain.GameOrders.Authority.SetMode, RimGovernor.Brain.GameOrders.Authority.SetMode> change) => new() { Operation = change(request.SetMode ?? throw new Exception("expected set-mode request")) };
    private static RimGovernor.Brain.GameOrders.Authority.ControlRequest WithRevoke(RimGovernor.Brain.GameOrders.Authority.ControlRequest request, Func<RimGovernor.Brain.GameOrders.Authority.Revoke, RimGovernor.Brain.GameOrders.Authority.Revoke> change) => new() { Operation = change(request.Revoke ?? throw new Exception("expected revoke request")) };
    private static void Failure(RimGovernor.Brain.GameFacts.Authority.ControlReply reply, RimGovernor.Brain.GameFacts.Common.FailureCode code)
    { Check(reply.Failure != null && (reply.Failure ?? throw new Exception("expected Failure reply")).Code == code, "expected " + code + ": " + reply); }
    private static void Reset()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map { uniqueID = 0 }; Find.TickManager = new TickManager();
        state = NativeControlAuthority.ForGame(Current.Game); time = 0;
        // Replace only the monotonic clock on the registered production state; no waits or GC.
        (typeof(NativeControlAuthority).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("authority clock field missing")).SetValue(state, (Func<long>)(() => time));
        NativeAuthorityHooks.Ready = true; State.SetHookHealth(true);
    }
    private static void Boundary()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        foreach (var json in new[] { "null", "{", "{\"unknown\":true}", "{\"setMode\":{\"expectedGeneration\":\"-1\"}}",
            "{\"acquire\":{\"expectedGeneration\":\"1\"}}", "{\"renew\":{\"expectedGeneration\":\"1\"}}" })
            Check(Undecodable(json) || NativeAuthorityControlTools.Apply(RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Authority.ControlRequest>(json) ?? new RimGovernor.Brain.GameOrders.Authority.ControlRequest()).Failure?.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
                "malformed control refused: " + json);
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "malformed control allocated state");
        Failure(Call(new RimGovernor.Brain.GameOrders.Authority.ControlRequest()), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest);
        Check(!NativeControlAuthority.TryGetForGame(Current.Game, out _), "missing operation allocated state");
    }
    private static void InvalidFields()
    {
        Reset(); var generation = State.Status().Generation;
        foreach (Func<RimGovernor.Brain.GameOrders.Authority.SetMode, RimGovernor.Brain.GameOrders.Authority.SetMode> mutate in new Func<RimGovernor.Brain.GameOrders.Authority.SetMode, RimGovernor.Brain.GameOrders.Authority.SetMode>[] {
            s => s with { ExpectedGeneration = 0 },
            // An absent expected generation, mode or reason is not expressible: the record requires each.
            s => s with { Identity = null } })
        {
            var request = WithSetMode(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto), mutate); Failure(Call(request), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest);
            Check(State.Status().Generation == generation && !State.Status().Active, "invalid set_mode mutated state");
        }
        Check(Undecodable(UndefinedEnum(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto), "mode")), "undefined set_mode mode refused before dispatch");
        Check(State.Status().Generation == generation && !State.Status().Active, "undefined set_mode mode mutated state");
        var stale = WithSetMode(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto), s => s with { Identity = (s.Identity ?? throw new Exception("set-mode fixture identity missing")) with { LoadToken = "old" } });
        Failure(Call(stale), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity);
        Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto, generation + 1)), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration);
        Check(State.Status().Generation == generation && !State.Status().Active, "refused set_mode mutated state");
    }
    private static void EagerInitialization()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        NativeAuthorityHooks.Ready = true;
        var initializations = NativeAuthorityHooks.Initializations;
        var request = WithSetMode(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto, 1), s => s with { Identity = (s.Identity ?? throw new Exception("set-mode fixture identity missing")) with { LoadToken = "stale" } });
        Failure(Call(request), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "stale set_mode initialized hooks");
        request = SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto, 1);
        // Manual before any Auto must not initialize hooks or allocate state: it has nothing to revoke.
        Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Manual, 1)), RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
        Failure(Call(Revoke(RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual, 1)), RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
        Check(NativeAuthorityHooks.Initializations == initializations && !NativeControlAuthority.TryGetForGame(Current.Game, out _), "Manual/Revoke initialized hooks");
        var reply = Call(request);
        Check(reply.Granted != null && NativeAuthorityHooks.Initializations == initializations + 1, "validated Auto did not eagerly initialize hooks");
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out var existing) && existing != null && existing.Status().Active, "eager Auto missing registered authority");
        Current.Game = new Game(); NativeAuthorityHooks.Ready = false;
        Failure(Call(request), RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
        Check(NativeControlAuthority.TryGetForGame(Current.Game, out existing) && existing != null && !existing.Status().Active && !existing.Status().Available, "unverified eager Auto granted authority");
    }
    private static void GrantsAndRevocations()
    {
        Reset(); var before = State.Status().Generation; var granted = Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto));
        Check(granted.Granted != null && (granted.Granted ?? throw new Exception("expected Granted reply")).Context?.NativeGeneration.IsAt(before + 1) == true, "Auto exact increment");
        var grant = granted.Granted ?? throw new Exception("expected authority grant");
        Check(grant.Authority?.Mode == RimGovernor.Brain.GameFacts.Authority.Mode.Auto, "grant reports Auto mode");
        Check(State.Status().Active && State.Status().Generation == before + 1, "grant did not activate state");
        // Repeating Auto is not a conflict: the single bot re-advances its own generation.
        var again = Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto));
        Check(again.Granted != null && (again.Granted ?? throw new Exception("expected Granted reply")).Context?.NativeGeneration.IsAt(before + 2) == true, "repeated Auto must advance");
        Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto, before + 1)), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration);
        Check(State.Status().Active && State.Status().Generation == before + 2, "stale Auto mutated authority");
        time = 1000;
        Check(State.Status().Active && State.Status().Generation == before + 2, "time alone revoked: there is no lease");
        var manual = Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Manual));
        Check(manual.Revoked != null && (manual.Revoked ?? throw new Exception("expected Revoked reply")).Context?.NativeGeneration.IsAt(before + 3) == true
            && (manual.Revoked ?? throw new Exception("expected Revoked reply")).Authority?.Reason == RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual, "Manual mode exact generation/reason");
        Check(!State.Status().Active, "Manual mode left authority active");
        var manualAgain = Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Manual));
        Check(manualAgain.Revoked != null && (manualAgain.Revoked ?? throw new Exception("expected Revoked reply")).Context?.NativeGeneration.IsAt(before + 4) == true, "repeated Manual must still advance");
        foreach (var reason in new[] { RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual, RimGovernor.Brain.GameFacts.Authority.RevocationReason.Disconnect, RimGovernor.Brain.GameFacts.Authority.RevocationReason.Shutdown })
        {
            if (!State.Status().Active) Check(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto)).Granted != null, "re-grant before revoke");
            var generation = State.Status().Generation;
            var reply = Call(Revoke(reason));
            Check(reply.Revoked != null && (reply.Revoked ?? throw new Exception("expected Revoked reply")).Context?.NativeGeneration.IsAt(generation + 1) == true && (reply.Revoked ?? throw new Exception("expected Revoked reply")).Authority?.Reason == reason, "revoke exact generation/reason");
            Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto, generation)), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration);
            Check(!State.Status().Active, "stale Auto reacquired after revoke");
        }
        // RimGovernor.Brain.GameFacts.Authority reasons the host may not assert itself: player detection, identity, hooks, exhaustion, terminal unavailability.
        foreach (var reason in new[] { RimGovernor.Brain.GameFacts.Authority.RevocationReason.None, RimGovernor.Brain.GameFacts.Authority.RevocationReason.PlayerControl,
            RimGovernor.Brain.GameFacts.Authority.RevocationReason.ExternalOrder, RimGovernor.Brain.GameFacts.Authority.RevocationReason.IdentityChanged, RimGovernor.Brain.GameFacts.Authority.RevocationReason.HooksUnavailable,
            RimGovernor.Brain.GameFacts.Authority.RevocationReason.GenerationExhausted, RimGovernor.Brain.GameFacts.Authority.RevocationReason.Unavailable })
            Failure(Call(Revoke(reason)), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest);
        Check(Undecodable(UndefinedEnum(Revoke(RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual), "reason")), "undefined revoke reason refused before dispatch");
        foreach (Func<RimGovernor.Brain.GameOrders.Authority.Revoke, RimGovernor.Brain.GameOrders.Authority.Revoke> mutate in new Func<RimGovernor.Brain.GameOrders.Authority.Revoke, RimGovernor.Brain.GameOrders.Authority.Revoke>[] {
            r => r with { ExpectedGeneration = 0 }, r => r with { Identity = null } })
        {
            var invalid = WithRevoke(Revoke(RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual), mutate);
            Failure(Call(invalid), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest);
        }
        Failure(Call(Revoke(RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual, State.Status().Generation + 1)), RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration);
        Reset(); Check(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto)).Granted != null, "grant");
        NativeAuthorityHooks.Ready = false; State.SetHookHealth(false);
        Check(!State.Status().Active && State.Status().Reason == NativeControlRevocationReason.HooksUnavailable, "hook loss did not revoke");
        Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto)), RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
        Check(!State.Status().Active, "unverified hooks granted");
        Reset(); Check(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto)).Granted != null, "grant");
        time = -1;
        Failure(Call(SetMode(RimGovernor.Brain.GameFacts.Authority.Mode.Auto)), RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
        Check(!State.Status().Active && !State.Status().Available, "clock fault granted");
    }
    internal static void Invoke()
    {
        Boundary(); InvalidFields(); EagerInitialization(); GrantsAndRevocations();
        Console.WriteLine($"Native authority Control: {checks} checks (production parser, identity, adapter and state; SDK/hook transport seams).");
    }
}
