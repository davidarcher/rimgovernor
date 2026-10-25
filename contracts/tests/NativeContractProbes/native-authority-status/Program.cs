using System;
using RimGovernor.Native;
using RimGovernor.Brain.GameAccess;
using Verse;

internal static class NativeAuthorityStatusProbe
{
    private static int checks;
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new RimGovernor.Brain.GameFacts.Common.ObservationContext
    {
        Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 },
        Tick = 0, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(1)
    };
    private static RimGovernor.Brain.GameFacts.Authority.Status Project(NativeControlSnapshot snapshot)
    {
        var status = NativeAuthorityTools.Project(snapshot, Context);
        var json = RimGovernor.Brain.GameOrders.OrdersJson.Serialize(status);
        Check(status.Equals(RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Authority.Status>(json)), "Official wire JSON roundtrip");
        Check(Context.NativeGeneration.IsAt(1), "Projection mutated caller observation context");
        Check(status.Context?.NativeGeneration.IsAt(snapshot.Generation) == true, "Projection lost refreshed generation");
        Check(status.Context?.Tick == 0,
            "Projection lost known tick/map zero");
        return status;
    }
    // The /native/{op} route decodes the request with OrdersJson before the core runs.
    private static RimGovernor.Brain.GameFacts.Authority.Status Read(RimGovernor.Brain.GameOrders.Authority.StatusRequest request) => NativeAuthorityTools.HandleReadStatus(
        RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Authority.StatusRequest>(RimGovernor.Brain.GameOrders.OrdersJson.Serialize(request)));
    // A refusal is a thrown NativeFailureException; null when the read answered.
    private static RimGovernor.Brain.GameFacts.Common.Failure? Refusal(RimGovernor.Brain.GameOrders.Authority.StatusRequest request)
    {
        try { Read(request); return null; }
        catch (NativeFailureException e) { return e.Value; }
    }
    private static void EndpointAdmission()
    {
        var game = new Game(); Current.Game = game; Find.CurrentMap = new Map { uniqueID = 0 };
        Find.TickManager = new TickManager { TicksGame = 0 };
        var missing = Refusal(new RimGovernor.Brain.GameOrders.Authority.StatusRequest());
        Check(missing != null, "Missing identity reaches typed failure");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Missing identity allocated authority");
        var request = new RimGovernor.Brain.GameOrders.Authority.StatusRequest { Identity = Context.Identity };
        for (int i = 0; i < 2; i++)
        {
            var absent = Read(request);
            Check(absent.Unavailable != null
                && (absent.Unavailable ?? throw new Exception("expected Unavailable reply")).Reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotObserved, "Missing state did not report unavailable");
            Check(absent.Context?. NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase, "Missing state fabricated authority generation");
            Check(!NativeControlAuthority.TryGetForGame(game, out _), "Read allocated native authority");
        }
        var stale = Refusal(new RimGovernor.Brain.GameOrders.Authority.StatusRequest { Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "stale", MapId = 0 } });
        Check(stale != null && stale.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity, "Stale identity accepted");
        Check(!NativeControlAuthority.TryGetForGame(game, out _), "Stale read allocated authority");
        var existing = NativeControlAuthority.ForGame(game);
        var known = Read(request);
        Check(known.Context?.NativeGeneration.IsAt(1) == true, "Existing generation omitted");
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
        Check(state.RevokeShutdown(43).Generation == 3 && (NativeControlAuthority.LastShutdown ?? throw new Exception("shutdown snapshot missing")).Tick == 43,
            "A second shutdown neither advances the generation nor loses the reason");

        Current.Game = null; Find.CurrentMap = null; Find.TickManager = null;
        var read = Read(new RimGovernor.Brain.GameOrders.Authority.StatusRequest { Identity = Context.Identity });
        Check(read != null && read.Inactive != null
            && (read.Inactive ?? throw new Exception("expected Inactive reply")).Reason == RimGovernor.Brain.GameFacts.Authority.RevocationReason.Shutdown, "Unloaded game reads Inactive(SHUTDOWN)");
        Check(read.Context?.Identity.Equals(Context.Identity) == true && read.Context?.Tick == 43
            && read.Context?.NativeGeneration.IsAt(3) == true, "Retained context carries the final identity, tick and generation");
        var unknown = Refusal(new RimGovernor.Brain.GameOrders.Authority.StatusRequest { Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "other", MapId = 0 } });
        Check(unknown != null && unknown.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable,
            "Another identity still fails with no game loaded");

        // Manual before the unload: the retained reason is Manual, not Shutdown.
        var next = new Game(); Current.Game = next; Find.CurrentMap = new Map { uniqueID = 1 };
        Find.TickManager = new TickManager { TicksGame = 7 };
        var nextIdentity = new NativeControlIdentity(next, Find.CurrentMap, "colony", "reload");
        var manual = new NativeControlAuthority(next, () => Current.Game == next ? nextIdentity : null, () => 0);
        manual.SetHookHealth(true);
        Check(manual.SetMode(1, NativeControlMode.Auto).Success && manual.SetMode(2, NativeControlMode.Manual).Success, "Manual fixture");
        Check(manual.RevokeShutdown(7).Generation == 3 && (NativeControlAuthority.LastShutdown ?? throw new Exception("shutdown snapshot missing")).Reason == NativeControlRevocationReason.Manual
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
        Check((initial.Unavailable ?? throw new Exception("expected Unavailable reply")).Reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotObserved, "Unverified hooks reason");
        Check(!state.Status().Active && !state.Status().Available, "Status mapping enabled authority");
        state.SetHookHealth(true);
        var granted = state.SetMode(state.Status().Generation, NativeControlMode.Auto);
        Check(granted.Success, "State fixture grant");
        var active = Project(granted.Snapshot);
        Check(active.Active != null, "Active branch");
        Check((active.Active ?? throw new Exception("expected Active reply")).Mode == RimGovernor.Brain.GameFacts.Authority.Mode.Auto, "Active authority is the bot's Auto mode");
        Check(active.Context?.NativeGeneration.IsAt(2) == true, "Active projection carries the granted generation");
        time = 1000;
        var still = Project(state.Status());
        Check(still.Active != null && still.Context?.NativeGeneration.IsAt(2) == true, "Time alone must not expire Auto mode");
        var manual = state.SetMode(2, NativeControlMode.Manual);
        var inactive = Project(manual.Snapshot);
        Check(inactive.Inactive != null && (inactive.Inactive ?? throw new Exception("expected Inactive reply")).Reason == RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual
            && inactive.Context?.NativeGeneration.IsAt(3) == true, "Manual mode maps to inactive with its generation");
        foreach (NativeControlRevocationReason reason in Enum.GetValues(typeof(NativeControlRevocationReason)))
        {
            if (reason == NativeControlRevocationReason.ClockUnavailable) continue;
            var wire = Project(new NativeControlSnapshot(identity, ulong.MaxValue, true, false, reason, null));
            Check(wire.Inactive != null, "Known reason inactive: " + reason);
            Check((wire.Inactive ?? throw new Exception("expected Inactive reply")).Reason.ToString() == reason.ToString(), "Explicit semantic reason mapping: " + reason);
        }
        var missing = Project(new NativeControlSnapshot(null, 5, false, false, NativeControlRevocationReason.IdentityChanged, null));
        Check(missing.Unavailable is { } missingUnavailable && missingUnavailable.Reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, "Missing identity unavailable");
        var clock = Project(new NativeControlSnapshot(identity, 6, false, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clock.Unavailable is { } clockUnavailable && clockUnavailable.Reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, "Clock failure is unavailable, not invented wire enum");
        var clockReason = Project(new NativeControlSnapshot(identity, 6, true, false, NativeControlRevocationReason.ClockUnavailable, null));
        Check(clockReason.Inactive != null && (clockReason.Inactive ?? throw new Exception("expected Inactive reply")).Reason == RimGovernor.Brain.GameFacts.Authority.RevocationReason.Unavailable,
            "Clock revocation maps to the wire's terminal Unavailable reason");
        var exhausted = Project(new NativeControlSnapshot(identity, ulong.MaxValue, false, false, NativeControlRevocationReason.GenerationExhausted, null));
        Check(exhausted.Unavailable is { } exhaustedUnavailable && exhaustedUnavailable.Reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.LimitExceeded, "Generation exhaustion unavailable");
        var unknown = Project(new NativeControlSnapshot(identity, 7, true, false, (NativeControlRevocationReason)999, null));
        Check(unknown.Unavailable != null, "Unknown internal enum leaked onto wire");
        EndpointAdmission();
        Shutdown();
        Console.WriteLine("Native authority status passed " + checks + " projection and endpoint-core assertions; the route and native gameplay remain separate.");
    }
}
