using System;
using System.Threading;
using HomeBridge.BridgeTools;
using Verse;

internal static class Program
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
        internal NativeControlSnapshot Acquire()
        {
            State.SetHookHealth(true);
            var result = State.Acquire(State.Status().Generation, "persistent-controller/α", 12, 1000);
            Assert(result.Success, "Explicit acquire failed");
            return result.Snapshot;
        }
    }

    private static void LeaseAndCas()
    {
        var h = new Harness();
        var initial = h.State.Status();
        Assert(initial.Generation == 1 && !initial.Available && !initial.Active, "Authority must start unavailable");
        Error(h.State.Acquire(1, "owner", 1, 1000), NativeControlError.Unavailable);
        Assert(!h.State.SetHookHealth(true).Active, "Hook health cannot grant authority");
        Error(h.State.Acquire(0, "owner", 1, 1000), NativeControlError.StaleGeneration);
        Error(h.State.Acquire(1, "owner", 0, 1000), NativeControlError.InvalidDirection);
        Error(h.State.Acquire(1, "owner", 1, 999), NativeControlError.InvalidLeaseDuration);
        Error(h.State.Acquire(1, "owner", 1, 30001), NativeControlError.InvalidLeaseDuration);
        Error(h.State.Acquire(1, " ", 1, 1000), NativeControlError.InvalidOwner);
        var acquired = h.Acquire();
        var lease = acquired.Lease!;
        Assert(acquired.Generation == 2 && acquired.RemainingLeaseMs == 1000, "Acquire must advance generation");
        Assert(lease.PlayerDirection == 12 && lease.ControllerSessionId == "persistent-controller/α", "Owner/direction lost");
        Error(h.State.Acquire(2, "other", 20, 1000), NativeControlError.OwnerConflict);
        Error(h.State.Check(1, lease.LeaseId, lease.ControllerSessionId), NativeControlError.StaleGeneration);
        Error(h.State.Check(2, lease.LeaseId, "other"), NativeControlError.OwnerConflict);
        Error(h.State.Check(2, "different", lease.ControllerSessionId), NativeControlError.LeaseMismatch);
        h.Time = 999;
        Assert(h.State.Status().RemainingLeaseMs == 1, "Lease expired early");
        var renewed = h.State.Renew(2, lease.LeaseId, lease.ControllerSessionId, 30000);
        Assert(renewed.Success && renewed.Snapshot.Generation == 2 && renewed.Snapshot.RemainingLeaseMs == 30000,
            "Renew changed generation or deadline incorrectly");
        Assert(renewed.Snapshot.Lease!.PlayerDirection == 12, "Renew changed player authority");
        h.Time = 30999;
        var expired = h.State.Status();
        Assert(!expired.Active && expired.Generation == 3 && expired.Reason == NativeControlRevocationReason.LeaseExpired, "Exact deadline must revoke");
        Assert(h.State.Status().Generation == 3, "Repeated status must not repeatedly revoke");
        Error(h.State.Renew(2, lease.LeaseId, lease.ControllerSessionId, 1000), NativeControlError.StaleGeneration);
        Error(h.State.Check(3, lease.LeaseId, lease.ControllerSessionId), NativeControlError.AuthorityRequired);
        Error(h.State.Acquire(2, lease.ControllerSessionId, 13, 1000), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Expired authority resurrected");
        Assert(h.State.Acquire(3, lease.ControllerSessionId, 13, 1000).Success, "New explicit direction could not acquire");
        var manual = h.State.Revoke(4, NativeControlRevocationReason.Manual);
        Assert(manual.Success && !manual.Snapshot.Active && manual.Snapshot.Generation == 5, "Manual did not revoke atomically");
        Error(h.State.Acquire(4, lease.ControllerSessionId, 14, 1000), NativeControlError.StaleGeneration);
    }

    private static void IdentityAndHealth()
    {
        var h = new Harness();
        var acquired = h.Acquire();
        h.Map = new Map { uniqueID = 1 };
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "load");
        Error(h.State.Check(acquired.Generation, acquired.Lease!.LeaseId, acquired.Lease.ControllerSessionId), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Same-ID replacement map retained authority");
        acquired = h.Acquire();
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "reloaded");
        Assert(!h.State.Status().Active && h.State.Status().Reason == NativeControlRevocationReason.IdentityChanged, "Load token did not revoke");
        acquired = h.Acquire();
        h.Context = null;
        Assert(h.State.Status().Identity == null && !h.State.Status().Available, "Missing context leaked old identity");
        var generation = h.State.Status().Generation;
        Assert(h.State.Status().Generation == generation, "Missing context incremented indefinitely");
        h.Context = new NativeControlIdentity(new Game(), h.Map, "colony", "reloaded");
        Error(h.State.Acquire(generation, "owner", 1, 1000), NativeControlError.StaleIdentity);
        h.Context = new NativeControlIdentity(h.Game, h.Map, "colony", "reloaded");
        Assert(!h.State.Status().Active, "Returning to prior game resumed authority");
        acquired = h.Acquire();
        var unhealthy = h.State.SetHookHealth(false);
        Assert(!unhealthy.Available && !unhealthy.Active && unhealthy.Generation > acquired.Generation, "Hook failure retained authority");
        Assert(!h.State.SetHookHealth(true).Active, "Restored hooks automatically resumed authority");
    }

    private static void OwnedScopes()
    {
        var h = new Harness();
        var acquired = h.Acquire();
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
        Assert(!revoked.Active && revoked.Generation > acquired.Generation, "External order did not revoke");
        h.Acquire();
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
        var acquired = h.Acquire();
        Assert(acquired.Generation == ulong.MaxValue, "Generation lost uint64 range");
        var revoked = h.State.Revoke(acquired.Generation, NativeControlRevocationReason.Manual);
        Assert(!revoked.Snapshot.Active && !revoked.Snapshot.Available && revoked.Snapshot.Generation == ulong.MaxValue,
            "Exhausted generation wrapped or stayed available");
        Error(h.State.Acquire(ulong.MaxValue, "owner", 1, 1000), NativeControlError.GenerationExhausted);
        h = new Harness(); h.Time = 100; h.Acquire(); h.Time = 99;
        Assert(!h.State.Status().Available && !h.State.Status().Active, "Backward monotonic clock retained authority");
        h.Time = 101;
        Assert(!h.State.Status().Available, "Broken clock silently recovered authority");
        h = new Harness(); acquired = h.Acquire(); h.Time = 1000;
        Error(h.State.Check(acquired.Generation, acquired.Lease!.LeaseId, acquired.Lease.ControllerSessionId), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Delayed write check failed to expire its lease");
    }

    private static void RegistryIsolation()
    {
        var first = new Game(); var second = new Game();
        Current.Game = first; Find.CurrentMap = new Map { uniqueID = 1 };
        var state = NativeControlAuthority.ForGame(first);
        Assert(ReferenceEquals(state, NativeControlAuthority.ForGame(first)), "Registry produced two owners for a Game");
        state.SetHookHealth(true);
        Assert(state.Acquire(1, "database-controller", 1, 1000).Success, "Native capture did not acquire");
        Current.Game = second;
        Assert(!state.Status().Available && !state.Status().Active, "Old Game state remained usable");
        var other = NativeControlAuthority.ForGame(second);
        Assert(!other.Status().Available && !other.Status().Active && other.Status().Generation == 1, "New Game inherited authority or hook health");
    }

    private static void ScopeAndClockBoundaries()
    {
        var h = new Harness(); h.Acquire();
        using (h.State.Owned())
        {
            h.Time = 1000;
            Assert(!h.State.IsOwned, "Expired scope still suppressed external hooks before Status");
            Assert(!h.State.Status().Active, "Scope expiry did not revoke lease");
        }
        h = new Harness(); h.Acquire();
        var scope = h.State.Owned();
        var manual = h.State.Revoke(h.State.Status().Generation, NativeControlRevocationReason.Manual);
        Assert(manual.Success && !h.State.IsOwned, "Manual revoke retained owned suppression");
        scope.Dispose(); scope.Dispose();
        Assert(!h.State.IsOwned, "Repeated Dispose corrupted owned scope");
        h = new Harness(); h.Time = long.MaxValue - 999; h.State.SetHookHealth(true);
        Error(h.State.Acquire(1, "owner", ulong.MaxValue, 1000), NativeControlError.Unavailable);
        Assert(!h.State.Status().Active && !h.State.Status().Available, "Deadline overflow granted a shortened lease");
        Assert(h.State.Status().Reason == NativeControlRevocationReason.ClockUnavailable, "Clock exhaustion not diagnosed");
        h = new Harness(); h.State.SetHookHealth(true);
        var granted = h.State.Acquire(1, "owner", ulong.MaxValue, 30000);
        Assert(granted.Success && granted.Snapshot.Lease!.PlayerDirection == ulong.MaxValue, "Player direction lost full uint64 range");
        h.Context = new NativeControlIdentity(h.Game, h.Map, "new-colony", "load");
        Error(h.State.Check(granted.Snapshot.Generation, granted.Snapshot.Lease!.LeaseId, "owner"), NativeControlError.StaleGeneration);
        Assert(!h.State.Status().Active, "Colony replacement retained old authority");
    }

    private static void CausalOrderExpiry()
    {
        var h = new Harness(); var lease = h.Acquire();
        string owner = lease.Lease!.ControllerSessionId; ulong direction = lease.Lease.PlayerDirection;
        using (h.State.Owned())
        {
            Assert(h.State.IsCausalScopeForOriginalOwner(owner,direction), "Original owner not captured");
            Assert(!h.State.IsCausalScopeForOriginalOwner("other",direction) && !h.State.IsCausalScopeForOriginalOwner(owner,direction+1), "Foreign causal owner accepted");
            h.Time = 1000;
            var expired = h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder);
            Assert(!expired.Active && expired.Generation == lease.Generation+1 && expired.Reason == NativeControlRevocationReason.LeaseExpired, "Order overwrote expiry or advanced twice");
            Assert(h.State.IsCausalScopeForOriginalOwner(owner,direction), "Expiry lost causal owner");
            Assert(!h.State.IsOwned, "Causal owner revived permission");
            Error(h.State.Check(lease.Generation,lease.Lease.LeaseId,owner), NativeControlError.StaleGeneration);
            bool refused=false; try { h.State.Owned(); } catch(InvalidOperationException) { refused=true; }
            Assert(refused,"New owned entry permitted after expiry");
            bool otherThread=true;var worker=new Thread(()=>otherThread=h.State.IsCausalScopeForOriginalOwner(owner,direction));worker.Start();worker.Join();
            Assert(!otherThread,"Causal scope escaped thread");
        }
        Assert(!h.State.IsCausalScopeForOriginalOwner(owner,direction),"Causal scope escaped disposal");
        Assert(h.State.RevokeExternal(NativeControlRevocationReason.ExternalOrder).Generation==lease.Generation+2,"Outside order suppressed");
        foreach(var reason in new[]{"manual","player","context","acquire","hooks","clock"})
        {
            h=new Harness();h.Acquire();
            using(h.State.Owned())
            {
                h.Time=1000;h.State.Status();
                if(reason=="manual") h.State.Revoke(h.State.Status().Generation,NativeControlRevocationReason.Manual);
                else if(reason=="player") h.State.RevokeExternal(NativeControlRevocationReason.PlayerControl);
                else if(reason=="context") h.State.RequestContextInvalidation();
                else if(reason=="acquire") Assert(h.State.Acquire(h.State.Status().Generation,owner,direction,1000).Success,"Reacquire failed");
                else if(reason=="hooks") h.State.SetHookHealth(false);
                else h.Time=-1;
                Assert(!h.State.IsCausalScopeForOriginalOwner(owner,direction),"Causal scope survived "+reason);
                Assert(!h.State.IsOwned,"Live scope survived "+reason);
            }
        }
    }

    private static void Main()
    {
        var transition = new Harness();
        var granted = transition.Acquire();
        using (transition.State.Owned())
        {
            var loader = new Thread(transition.State.RequestContextInvalidation);
            loader.Start(); loader.Join();
            Assert(!transition.State.IsOwned, "Queued context replacement retained owned suppression");
            Error(transition.State.Check(granted.Generation, granted.Lease!.LeaseId, "persistent-controller/α"), NativeControlError.StaleGeneration);
            Assert(transition.State.Status().Reason == NativeControlRevocationReason.IdentityChanged, "Queued transition lost its reason");
            var refreshed = transition.State.Status().Generation;
            Assert(transition.State.Status().Generation == refreshed, "Queued invalidation applied more than once");
        }
        LeaseAndCas(); IdentityAndHealth(); OwnedScopes(); ExhaustionAndClock(); RegistryIsolation(); ScopeAndClockBoundaries(); CausalOrderExpiry();
        Console.WriteLine("Native authority state passed " + assertions + " assertions (production source, injected clock/context).");
    }
}
