#nullable disable
using System;
using HomeBridge.BridgeTools;
using RimGovernor.Brain.Bridge.Contracts;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Wire = RimGovernor.Contracts.Wire.Authority;

internal static class NativeAuthorityStatusProbe
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static readonly Common.ObservationContext Context = new Common.ObservationContext
    {
        Identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 },
        Tick = 0, NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.AtCase(1)
    };
    private static Wire.Status Project(NativeControlSnapshot snapshot)
    {
        var status = NativeAuthorityTools.Project(snapshot, Context);
        var json = RimGovernor.Contracts.Wire.WireJson.Serialize(status);
        Check(status.Equals(RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.Status>(json)), "Official wire JSON roundtrip");
        Check(Context.NativeGeneration.IsAt(1), "Projection mutated caller observation context");
        Check(status.Context.NativeGeneration.IsAt(snapshot.Generation), "Projection lost refreshed generation");
        Check(status.Context.Tick == 0,
            "Projection lost known tick/map zero");
        return status;
    }
    // The /native/{op} route decodes the request with WireJson before the core runs.
    private static Wire.Status Read(Wire.StatusRequest request) => NativeAuthorityTools.HandleReadStatus(
        RimGovernor.Contracts.Wire.WireJson.Deserialize<Wire.StatusRequest>(RimGovernor.Contracts.Wire.WireJson.Serialize(request)));
    // A refusal is a thrown NativeFailureException; null when the read answered.
    private static Common.Failure Refusal(Wire.StatusRequest request)
    {
        try { Read(request); return null; }
        catch (NativeFailureException e) { return e.Value; }
    }
    private static void EndpointAdmission()
    {
        var game = new Game(); Current.Game = game; Find.CurrentMap = new Map { uniqueID = 0 };
        Find.TickManager = new TickManager { TicksGame = 0 };
        var missing = Refusal(new Wire.StatusRequest());
        Check(missing != null, "Missing identity reaches typed failure");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Missing identity allocated authority");
        var request = new Wire.StatusRequest { Identity = Context.Identity };
        for (int i = 0; i < 2; i++)
        {
            var absent = Read(request);
            Check(absent.Unavailable != null
                && absent.Unavailable.Reason == Common.UnavailableReason.NotObserved, "Missing state did not report unavailable");
            Check(absent.Context. NativeGeneration is RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase, "Missing state fabricated authority generation");
            Check(!NativeControlAuthority.TryGetForGame(game, out _), "Read allocated native authority");
        }
        var stale = Refusal(new Wire.StatusRequest { Identity = new Common.Identity { ColonyId = "colony", LoadToken = "stale", MapId = 0 } });
        Check(stale != null && stale.Code == Common.FailureCode.StaleIdentity, "Stale identity accepted");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Stale read allocated authority");
        var existing = NativeControlAuthority.ForGame(game);
        var known = Read(request);
        Check(known.Context.NativeGeneration.IsAt(1), "Existing generation omitted");
        Check(known.Unavailable != null
            && !existing.Status().Available && !existing.Status().Active, "Read enabled unverified authority");
    }
    // Orderly exit: an unloaded game's final authority stays readable for
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
        var read = Read(new Wire.StatusRequest { Identity = Context.Identity });
        Check(read != null && read.Inactive != null
            && read.Inactive.Reason == Wire.RevocationReason.Shutdown, "Unloaded game reads Inactive(SHUTDOWN)");
        Check(read.Context.Identity.Equals(Context.Identity) && read.Context.Tick == 43
            && read.Context.NativeGeneration.IsAt(3), "Retained context carries the final identity, tick and generation");
        var unknown = Refusal(new Wire.StatusRequest { Identity = new Common.Identity { ColonyId = "colony", LoadToken = "other", MapId = 0 } });
        Check(unknown != null && unknown.Code == Common.FailureCode.Unavailable,
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
        Check(initial.Unavailable != null, "Unverified hooks must report unavailable");
        Check(initial.Unavailable.Reason == Common.UnavailableReason.NotObserved, "Unverified hooks reason");
        Check(!state.Status().Active && !state.Status().Available, "Status mapping enabled authority");
        state.SetHookHealth(true);
        var granted = state.SetMode(state.Status().Generation, NativeControlMode.Auto);
        Check(granted.Success, "State fixture grant");
        var active = Project(granted.Snapshot);
        Check(active.Active != null, "Active branch");
        Check(active.Active.Mode == Wire.Mode.Auto, "Active authority is the bot's Auto mode");
        Check(active.Context.NativeGeneration.IsAt(2), "Active projection carries the granted generation");
        time = 1000;
        var still = Project(state.Status());
        Check(still.Active != null && still.Context.NativeGeneration.IsAt(2), "Time alone must not expire Auto mode");
        var manual = state.SetMode(2, NativeControlMode.Manual);
        var inactive = Project(manual.Snapshot);
        Check(inactive.Inactive != null && inactive.Inactive.Reason == Wire.RevocationReason.Manual
            && inactive.Context.NativeGeneration.IsAt(3), "Manual mode maps to inactive with its generation");
        foreach (NativeControlRevocationReason reason in Enum.GetValues(typeof(NativeControlRevocationReason)))
        {
            if (reason == NativeControlRevocationReason.ClockUnavailable) continue;
            var wire = Project(new NativeControlSnapshot(identity, ulong.MaxValue, true, false, reason, null));
            Check(wire.Inactive != null, "Known reason inactive: " + reason);
            Check(wire.Inactive.Reason.ToString() == reason.ToString(), "Explicit semantic reason mapping: " + reason);
        }
        var missing = Project(new NativeControlSnapshot(null, 5, false, false, NativeControlRevocationReason.IdentityChanged, null));
        Check(missing.Unavailable.Reason == Common.UnavailableReason.NotLoaded, "Missing identity unavailable");
        var clock = Project(new NativeControlSnapshot(identity, 6, false, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clock.Unavailable.Reason == Common.UnavailableReason.ReadFailed, "Clock failure is unavailable, not invented wire enum");
        var clockReason = Project(new NativeControlSnapshot(identity, 6, true, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clockReason.Inactive != null && clockReason.Inactive.Reason == Wire.RevocationReason.Unavailable,
            "Clock revocation maps to the wire's terminal Unavailable reason");
        var exhausted = Project(new NativeControlSnapshot(identity, ulong.MaxValue, false, false, NativeControlRevocationReason.GenerationExhausted, null));
        Check(exhausted.Unavailable.Reason == Common.UnavailableReason.LimitExceeded, "Generation exhaustion unavailable");
        var unknown = Project(new NativeControlSnapshot(identity, 7, true, false, (NativeControlRevocationReason)999, null));
        Check(unknown.Unavailable != null, "Unknown internal enum leaked onto wire");
        EndpointAdmission();
        Shutdown();
        Console.WriteLine("Native authority status passed " + checks + " projection and endpoint-core assertions; the route and native gameplay remain separate.");
    }
}
