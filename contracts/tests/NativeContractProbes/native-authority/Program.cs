#nullable enable
using System;
using System.Threading;
using HomeBridge.BridgeTools;
using Verse;

// Exercises the production NativeControlAuthority with an injected clock and
// context. Authority is Mode + generation (#52): SetMode(Auto) grants the bot
// outright, SetMode(Manual)/Revoke/external events revoke, and every
// transition advances the generation so a write computed against an older
// colony state is refused as StaleGeneration. There is no lease to expire.
internal static class NativeAuthorityProbe
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static void Error(NativeControlResult result, NativeControlError error)
    { Assert(!result.Success && result.Error == error, "Expected " + error + ", got " + result.Error); }

    private sealed class Harness
    {
        internal readonly Game Game = new Game();
        internal Map Map = new Map { uniqueID = 1 };
        internal long Time;
        internal NativeControlIdentity? Context;
        internal readonly NativeControlAuthority State;
        internal Harness(ulong generation = 1)
        {
            Context = new NativeControlIdentity(Game, Map, "colony", "load");
            State = new NativeControlAuthority(Game, () => Context, () => Time, generation);
        }
        internal NativeControlSnapshot Auto()
        {
            State.SetHookHealth(true);
            var result = State.SetMode(State.Status().Generation, NativeControlMode.Auto);
            Assert(result.Success, "Explicit Auto mode failed: " + result.Error);
            return result.Snapshot;
        }
    }

    private static void ModeAndCas()
    {
        var h = new Harness();
        var initial = h.State.Status();
        Assert(initial.Generation == 1 && !initial.Available && !initial.Active, "Authority must start unavailable");
        Error(h.State.SetMode(1, NativeControlMode.Auto), NativeControlError.Unavailable);
        Assert(!h.State.SetHookHealth(true).Active, "Hook health cannot grant authority");
        Error(h.State.SetMode(0, NativeControlMode.Auto), NativeControlError.StaleGeneration);
        Error(h.State.SetMode(2, NativeControlMode.Auto), NativeControlError.StaleGeneration);
        var granted = h.Auto();
        Assert(granted.Generation == 2 && granted.Active && granted.Reason == NativeControlRevocationReason.None, "Auto must advance generation and activate");
        Error(h.State.SetMode(1, NativeControlMode.Auto), NativeControlError.StaleGeneration);
        Error(h.State.Check(1), NativeControlError.StaleGeneration);
        Error(h.State.Check(0), NativeControlError.StaleGeneration);
        Assert(h.State.Check(2).Success, "Current generation check refused");
        Assert(h.State.Check(2).Snapshot.Generation == 2, "Check must not advance the generation");
        h.Time = 30999;
        Assert(h.State.Status().Active && h.State.Status().Generation == 2, "Time alone must not revoke: there is no lease");
        var again = h.State.SetMode(2, NativeControlMode.Auto);
        Assert(again.Success && again.Snapshot.Active && again.Snapshot.Generation == 3, "Repeated Auto must still advance the generation");
        Error(h.State.Check(2), NativeControlError.StaleGeneration);
        var manual = h.State.SetMode(3, NativeControlMode.Manual);
        Assert(manual.Success && !manual.Snapshot.Active && manual.Snapshot.Generation == 4
            && manual.Snapshot.Reason == NativeControlRevocationReason.Manual, "Manual mode did not revoke atomically");
        Error(h.State.Check(4), NativeControlError.AuthorityRequired);
        Error(h.State.Check(3), NativeControlError.StaleGeneration);
        Error(h.State.SetMode(3, NativeControlMode.Auto), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Manual authority resurrected");
        Assert(h.State.SetMode(4, NativeControlMode.Auto).Success, "Auto after Manual refused");
        var revoked = h.State.Revoke(5, NativeControlRevocationReason.Manual);
        Assert(revoked.Success && !revoked.Snapshot.Active && revoked.Snapshot.Generation == 6, "Revoke did not advance atomically");
        Error(h.State.Revoke(5, NativeControlRevocationReason.Manual), NativeControlError.StaleGeneration);
        Error(h.State.Revoke(0, NativeControlRevocationReason.Manual), NativeControlError.StaleGeneration);
        Error(h.State.SetMode(5, NativeControlMode.Auto), NativeControlError.StaleGeneration);
    }

    private static void IdentityAndHealth()
    {
        var h = new Harness();
        var granted = h.Auto();
        h.Map = new Map { uniqueID = 1 };
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "load");
        Error(h.State.Check(granted.Generation), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Same-ID replacement map retained authority");
        granted = h.Auto();
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "reloaded");
        Assert(!h.State.Status().Active && h.State.Status().Reason == NativeControlRevocationReason.IdentityChanged, "Load token did not revoke");
        granted = h.Auto();
        h.Context = null;
        Assert(h.State.Status().Identity == null && !h.State.Status().Available, "Missing context leaked old identity");
        var generation = h.State.Status().Generation;
        Assert(h.State.Status().Generation == generation, "Missing context incremented indefinitely");
        h.Context = new NativeControlIdentity(new Game(), h.Map, "colony", "reloaded");
        Error(h.State.SetMode(generation, NativeControlMode.Auto), NativeControlError.StaleIdentity);
        Error(h.State.Revoke(generation, NativeControlRevocationReason.Manual), NativeControlError.StaleIdentity);
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "reloaded");
        Assert(!h.State.Status().Active, "Returning to prior game resumed authority");
        granted = h.Auto();
        var unhealthy = h.State.SetHookHealth(false);
        Assert(!unhealthy.Available && !unhealthy.Active && unhealthy.Generation > granted.Generation
            && unhealthy.Reason == NativeControlRevocationReason.HooksUnavailable, "Hook failure retained authority");
        Assert(!h.State.SetHookHealth(true).Active, "Restored hooks automatically resumed authority");
        Error(h.State.SetMode(granted.Generation, NativeControlMode.Auto), NativeControlError.StaleGeneration);
    }

    private static void OwnedScopes()
    {
        var h = new Harness();
        var granted = h.Auto();
        try
        {
            using (h.State.Owned())
            {
                Assert(h.State.IsOwned, "Owned scope missing");
                using (h.State.Owned())
                    Assert(h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder).Active, "Owned hook self-revoked");
                Assert(h.State.IsOwned, "Nested disposal removed outer scope");
                bool otherOwned = true, rejected = false;
                var worker = new Thread(() => {
                    otherOwned = h.State.IsOwned;
                    try { h.State.Status(); } catch (InvalidOperationException) { rejected = true; }
                });
                worker.Start(); worker.Join();
                Assert(!otherOwned && rejected, "Owned state leaked across threads");
                throw new ApplicationException();
            }
        }
        catch (ApplicationException) { }
        Assert(!h.State.IsOwned, "Exception leaked owned scope");
        var revoked = h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder);
        Assert(!revoked.Active && revoked.Generation > granted.Generation
            && revoked.Reason == NativeControlRevocationReason.ExternalOrder, "External order did not revoke");
        h.Auto();
        var player = h.State.RevokeExternal(NativeControlRevocationReason.PlayerControl);
        Assert(!player.Active && player.Reason == NativeControlRevocationReason.PlayerControl, "Player control did not revoke");
        bool refused = false; try { h.State.Owned(); } catch (InvalidOperationException) { refused = true; }
        Assert(refused, "Owned entry permitted without authority");
        h.Auto();
        using (h.State.Owned())
        {
            h.Context = new NativeControlIdentity(h.Game, new Map { uniqueID = 1 }, "colony", "load");
            h.State.Status();
            Assert(!h.State.IsOwned, "Old map scope suppressed new map hooks");
        }
    }

    private static void ExhaustionAndClock()
    {
        var h = new Harness(ulong.MaxValue - 1);
        var granted = h.Auto();
        Assert(granted.Generation == ulong.MaxValue, "Generation lost uint64 range");
        var revoked = h.State.Revoke(granted.Generation, NativeControlRevocationReason.Manual);
        Assert(!revoked.Success && revoked.Error == NativeControlError.GenerationExhausted, "Final revoke must report exhaustion");
        Assert(!revoked.Snapshot.Active && !revoked.Snapshot.Available && revoked.Snapshot.Generation == ulong.MaxValue
            && revoked.Snapshot.Reason == NativeControlRevocationReason.GenerationExhausted, "Exhausted generation wrapped or stayed available");
        Error(h.State.SetMode(ulong.MaxValue, NativeControlMode.Auto), NativeControlError.GenerationExhausted);
        Error(h.State.Check(ulong.MaxValue), NativeControlError.GenerationExhausted);
        h = new Harness(ulong.MaxValue);
        h.State.SetHookHealth(true);
        Error(h.State.SetMode(ulong.MaxValue, NativeControlMode.Auto), NativeControlError.GenerationExhausted);
        Assert(!h.State.Status().Active && !h.State.Status().Available, "Auto at the last generation must exhaust, not activate");
        h = new Harness(); h.Time = 100; h.Auto(); h.Time = 99;
        Assert(!h.State.Status().Available && !h.State.Status().Active, "Backward monotonic clock retained authority");
        Assert(h.State.Status().Reason == NativeControlRevocationReason.ClockUnavailable, "Clock failure not diagnosed");
        h.Time = 101;
        Assert(!h.State.Status().Available, "Broken clock silently recovered authority");
        Error(h.State.SetMode(h.State.Status().Generation, NativeControlMode.Auto), NativeControlError.Unavailable);
        h = new Harness(); h.Time = 100; h.Auto(); h.Time = -1;
        Assert(!h.State.Status().Available && h.State.Status().Reason == NativeControlRevocationReason.ClockUnavailable, "Negative clock retained authority");
    }

    private static void RegistryIsolation()
    {
        var first = new Game(); var second = new Game();
        Current.Game = first; Find.CurrentMap = new Map { uniqueID = 1 };
        var state = NativeControlAuthority.ForGame(first);
        Assert(ReferenceEquals(state, NativeControlAuthority.ForGame(first)), "Registry produced two owners for a Game");
        Assert(NativeControlAuthority.TryGetForGame(first, out var found) && ReferenceEquals(found, state), "Registry lookup lost the Game's authority");
        Assert(!NativeControlAuthority.TryGetForGame(second, out _), "Lookup created authority for an unseen Game");
        state.SetHookHealth(true);
        Assert(state.SetMode(1, NativeControlMode.Auto).Success, "Native capture did not grant Auto");
        Current.Game = second;
        Assert(!state.Status().Available && !state.Status().Active, "Old Game state remained usable");
        var other = NativeControlAuthority.ForGame(second);
        Assert(!other.Status().Available && !other.Status().Active && other.Status().Generation == 1, "New Game inherited authority or hook health");
    }

    private static void ScopeBoundaries()
    {
        var h = new Harness(); h.Auto();
        var scope = h.State.Owned();
        var manual = h.State.Revoke(h.State.Status().Generation, NativeControlRevocationReason.Manual);
        Assert(manual.Success && !h.State.IsOwned, "Manual revoke retained owned suppression");
        scope.Dispose(); scope.Dispose();
        Assert(!h.State.IsOwned, "Repeated Dispose corrupted owned scope");
        h = new Harness(); h.Auto();
        using (h.State.Owned())
        {
            var readvanced = h.State.SetMode(h.State.Status().Generation, NativeControlMode.Auto);
            Assert(readvanced.Success && readvanced.Snapshot.Active, "Auto inside a scope refused");
            Assert(!h.State.IsOwned, "Generation advance kept the older owned scope alive");
            bool refused = false; try { h.State.Owned(); } catch (InvalidOperationException) { refused = true; }
            Assert(refused, "Nested entry permitted after the outer scope's generation lapsed");
        }
        h = new Harness(); var granted = h.Auto();
        h.Context = new NativeControlIdentity(h.Game, h.Map, "new-colony", "load");
        Error(h.State.Check(granted.Generation), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Colony replacement retained old authority");
    }

    private static void CausalScope()
    {
        var h = new Harness(); var granted = h.Auto();
        Assert(!h.State.IsCausalOwnedScope(), "Causal scope reported outside Owned()");
        using (h.State.Owned())
        {
            Assert(h.State.IsCausalOwnedScope(), "Owned scope not causal");
            var order = h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder);
            Assert(order.Active && order.Generation == granted.Generation, "Own order revoked its own scope");
            Assert(h.State.IsCausalOwnedScope() && h.State.IsOwned, "Own order lost the causal scope");
            bool otherThread = true; var worker = new Thread(() => otherThread = h.State.IsCausalOwnedScope()); worker.Start(); worker.Join();
            Assert(!otherThread, "Causal scope escaped thread");
        }
        Assert(!h.State.IsCausalOwnedScope(), "Causal scope escaped disposal");
        Assert(h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder).Generation == granted.Generation + 1, "Outside order suppressed");
        foreach (var reason in new[] { "manual", "revoke", "context", "auto", "hooks", "clock", "map" })
        {
            h = new Harness(); h.Time = 100; h.Auto();
            using (h.State.Owned())
            {
                Assert(h.State.IsCausalOwnedScope(), "Scope not causal before " + reason);
                if (reason == "manual") Assert(h.State.SetMode(h.State.Status().Generation, NativeControlMode.Manual).Success, "Manual failed");
                else if (reason == "revoke") Assert(h.State.Revoke(h.State.Status().Generation, NativeControlRevocationReason.Manual).Success, "Revoke failed");
                else if (reason == "context") h.State.RequestContextInvalidation();
                else if (reason == "auto") Assert(h.State.SetMode(h.State.Status().Generation, NativeControlMode.Auto).Success, "Auto failed");
                else if (reason == "hooks") h.State.SetHookHealth(false);
                else if (reason == "clock") h.Time = 99;
                else h.Context = new NativeControlIdentity(h.Game, new Map { uniqueID = 2 }, "colony", "load");
                Assert(!h.State.IsCausalOwnedScope(), "Causal scope survived " + reason);
                Assert(!h.State.IsOwned, "Live scope survived " + reason);
                if (reason != "auto") Assert(!h.State.Status().Active, "Authority survived " + reason);
            }
        }
    }

    internal static void Invoke()
    {
        var transition = new Harness();
        var granted = transition.Auto();
        using (transition.State.Owned())
        {
            var loader = new Thread(transition.State.RequestContextInvalidation);
            loader.Start(); loader.Join();
            Assert(!transition.State.IsOwned, "Queued context replacement retained owned suppression");
            Error(transition.State.Check(granted.Generation), NativeControlError.StaleGeneration);
            Assert(transition.State.Status().Reason == NativeControlRevocationReason.IdentityChanged, "Queued transition lost its reason");
            var refreshed = transition.State.Status().Generation;
            Assert(transition.State.Status().Generation == refreshed, "Queued invalidation applied more than once");
        }
        ModeAndCas(); IdentityAndHealth(); OwnedScopes(); ExhaustionAndClock(); RegistryIsolation(); ScopeBoundaries(); CausalScope();
        Console.WriteLine("Native authority state passed " + assertions + " assertions (production source, injected clock/context).");
    }
}
