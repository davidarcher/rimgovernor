#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Verse;

namespace HomeBridge.BridgeTools
{
    public enum NativeControlRevocationReason
    {
        None, Manual, PlayerDirection, ExternalOrder, PlayerControl, LeaseExpired,
        IdentityChanged, Disconnect, Shutdown, HooksUnavailable, GenerationExhausted, ClockUnavailable
    }

    public enum NativeControlError
    {
        None, Unavailable, StaleIdentity, StaleGeneration, InvalidDirection, InvalidLeaseDuration,
        InvalidOwner, AuthorityRequired, OwnerConflict, LeaseMismatch, GenerationExhausted
    }

    public sealed class NativeControlIdentity
    {
        public NativeControlIdentity(Game game, Map map, string colonyId, string loadToken)
        { Game = game; Map = map; ColonyId = colonyId; LoadToken = loadToken; MapId = map.uniqueID; }
        public Game Game { get; }
        public Map Map { get; }
        public string ColonyId { get; }
        public string LoadToken { get; }
        public int MapId { get; }

        internal bool Same(NativeControlIdentity other) => ReferenceEquals(Game, other.Game)
            && ReferenceEquals(Map, other.Map) && MapId == other.MapId && ColonyId == other.ColonyId && LoadToken == other.LoadToken;
    }

    public sealed class NativeControlLease
    {
        internal NativeControlLease(string leaseId, string controllerSessionId, ulong playerDirection)
        { LeaseId = leaseId; ControllerSessionId = controllerSessionId; PlayerDirection = playerDirection; }
        public string LeaseId { get; }
        public string ControllerSessionId { get; }
        public ulong PlayerDirection { get; }
    }

    public sealed class NativeControlSnapshot
    {
        internal NativeControlSnapshot(NativeControlIdentity? identity, ulong generation, bool available,
            NativeControlLease? lease, int remaining, NativeControlRevocationReason reason)
        { Identity = identity; Generation = generation; Available = available; Lease = lease; RemainingLeaseMs = remaining; Reason = reason; }
        public NativeControlIdentity? Identity { get; }
        public ulong Generation { get; }
        public bool Available { get; }
        public bool Active => Lease != null;
        public NativeControlLease? Lease { get; }
        public int RemainingLeaseMs { get; }
        public NativeControlRevocationReason Reason { get; }
    }

    public sealed class NativeControlResult
    {
        internal NativeControlResult(NativeControlError error, NativeControlSnapshot snapshot)
        { Error = error; Snapshot = snapshot; }
        public bool Success => Error == NativeControlError.None;
        public NativeControlError Error { get; }
        public NativeControlSnapshot Snapshot { get; }
    }

    /// <summary>
    /// Unsaved, main-thread authority for one Game. Clock supervision is separate.
    /// Only the explicit player-control adapter may acquire; executors may check or renew.
    /// </summary>
    public sealed class NativeControlAuthority
    {
        private static readonly ConditionalWeakTable<Game, NativeControlAuthority> States = new ConditionalWeakTable<Game, NativeControlAuthority>();
        private readonly Game game;
        private readonly Func<NativeControlIdentity?> context;
        private readonly Func<long> clock;
        private readonly int thread;
        private NativeControlIdentity? identity;
        private NativeControlLease? lease;
        private ulong generation;
        private long deadline;
        private long now;
        private bool observedClock;
        private bool clockHealthy = true;
        private bool hooksReady;
        private bool contextValid;
        private bool contextLost;
        private bool exhausted;
        private int ownedDepth;
        private NativeControlIdentity? ownedIdentity;
        private ulong ownedGeneration;
        private NativeControlRevocationReason reason = NativeControlRevocationReason.HooksUnavailable;

        public static NativeControlAuthority ForGame(Game game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            return States.GetValue(game, value => new NativeControlAuthority(value, Capture, NewClock()));
        }

        internal NativeControlAuthority(Game game, Func<NativeControlIdentity?> context, Func<long> clock, ulong initialGeneration = 1)
        {
            this.game = game ?? throw new ArgumentNullException(nameof(game));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (initialGeneration == 0) throw new ArgumentOutOfRangeException(nameof(initialGeneration));
            generation = initialGeneration;
            thread = Thread.CurrentThread.ManagedThreadId;
        }

        private static Func<long> NewClock()
        {
            var watch = Stopwatch.StartNew();
            return () => watch.ElapsedMilliseconds;
        }

        private static NativeControlIdentity? Capture()
        {
            var current = Current.Game;
            var map = Find.CurrentMap;
            var saved = current?.GetComponent<ColonyIdentity>();
            return current == null || map == null || saved == null
                || string.IsNullOrEmpty(saved.ColonyId) || string.IsNullOrEmpty(saved.LoadToken)
                ? null : new NativeControlIdentity(current, map, saved.ColonyId, saved.LoadToken);
        }

        public NativeControlSnapshot Status() { Refresh(); return Snapshot(); }

        public NativeControlSnapshot SetHookHealth(bool ready)
        {
            Refresh();
            if (!ready && hooksReady) Invalidate(NativeControlRevocationReason.HooksUnavailable);
            hooksReady = ready;
            return Snapshot();
        }

        public NativeControlResult Acquire(ulong expectedGeneration, string controllerSessionId, ulong playerDirection, int leaseMs)
        {
            Refresh();
            var error = Guard(expectedGeneration);
            if (error != NativeControlError.None) return Result(error);
            if (string.IsNullOrWhiteSpace(controllerSessionId)) return Result(NativeControlError.InvalidOwner);
            if (playerDirection == 0) return Result(NativeControlError.InvalidDirection);
            if (!ValidDuration(leaseMs)) return Result(NativeControlError.InvalidLeaseDuration);
            if (lease != null) return Result(NativeControlError.OwnerConflict);
            if (!TryDeadline(leaseMs, out var expires)) return Result(NativeControlError.Unavailable);
            if (!Advance()) return Result(NativeControlError.GenerationExhausted);
            lease = new NativeControlLease(Guid.NewGuid().ToString("N"), controllerSessionId, playerDirection);
            deadline = expires;
            reason = NativeControlRevocationReason.None;
            return Result(NativeControlError.None);
        }

        public NativeControlResult Renew(ulong expectedGeneration, string leaseId, string controllerSessionId, int leaseMs)
        {
            Refresh();
            var error = CheckLease(expectedGeneration, leaseId, controllerSessionId);
            if (error != NativeControlError.None) return Result(error);
            if (!ValidDuration(leaseMs)) return Result(NativeControlError.InvalidLeaseDuration);
            if (!TryDeadline(leaseMs, out var expires)) return Result(NativeControlError.Unavailable);
            deadline = expires;
            return Result(NativeControlError.None);
        }

        public NativeControlResult Check(ulong expectedGeneration, string leaseId, string controllerSessionId)
        {
            Refresh();
            return Result(CheckLease(expectedGeneration, leaseId, controllerSessionId));
        }

        public NativeControlResult Revoke(ulong expectedGeneration, NativeControlRevocationReason revokeReason)
        {
            Refresh();
            if (!contextValid) return Result(NativeControlError.StaleIdentity);
            if (expectedGeneration == 0 || expectedGeneration != generation) return Result(NativeControlError.StaleGeneration);
            Invalidate(revokeReason);
            return Result(exhausted ? NativeControlError.GenerationExhausted : NativeControlError.None);
        }

        /// <summary>Actual native player actions revoke even without a caller lease.</summary>
        public NativeControlSnapshot RevokeExternal(NativeControlRevocationReason revokeReason)
        {
            Refresh();
            if (contextValid && !IsOwned) Invalidate(revokeReason);
            return Snapshot();
        }

        /// <summary>Synchronous admitted work only; never carry this scope across await.</summary>
        public IDisposable Owned()
        {
            Refresh();
            if (!Available || lease == null) throw new InvalidOperationException("Native authority is not active");
            if (ownedDepth > 0 && !IsOwned) throw new InvalidOperationException("Native owned scope identity changed");
            if (ownedDepth == 0) { ownedIdentity = identity; ownedGeneration = generation; }
            ownedDepth = checked(ownedDepth + 1);
            return new OwnedScope(this);
        }

        public bool IsOwned
        {
            get
            {
                if (Thread.CurrentThread.ManagedThreadId != thread || ownedDepth == 0) return false;
                Refresh();
                return Available && lease != null && ownedGeneration == generation && CurrentContextMatches();
            }
        }

        private sealed class OwnedScope : IDisposable
        {
            private NativeControlAuthority? owner;
            internal OwnedScope(NativeControlAuthority owner) { this.owner = owner; }
            public void Dispose()
            {
                if (owner == null) return;
                owner.RequireThread();
                owner.ownedDepth--;
                if (owner.ownedDepth == 0) owner.ownedIdentity = null;
                owner = null;
            }
        }

        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Native authority must run on its owning main thread");
        }

        private bool CurrentContextMatches()
        {
            try
            {
                var current = context();
                return contextValid && current != null && ownedIdentity != null && ownedIdentity.Same(current);
            }
            catch { return false; }
        }

        private void Refresh()
        {
            RequireThread();
            try
            {
                var next = clock();
                if (next < 0 || observedClock && next < now) throw new InvalidOperationException();
                now = next;
                observedClock = true;
            }
            catch
            {
                if (clockHealthy) Invalidate(NativeControlRevocationReason.ClockUnavailable);
                clockHealthy = false;
            }
            NativeControlIdentity? current;
            try { current = context(); }
            catch { current = null; }
            contextValid = current != null && ReferenceEquals(current.Game, game);
            if (!contextValid)
            {
                if (!contextLost && identity != null) Invalidate(NativeControlRevocationReason.IdentityChanged);
                contextLost = true;
                return;
            }
            if (identity != null && !identity.Same(current!)) Invalidate(NativeControlRevocationReason.IdentityChanged);
            identity = current;
            contextLost = false;
            if (lease != null && now >= deadline) Invalidate(NativeControlRevocationReason.LeaseExpired);
        }

        private bool Available => contextValid && hooksReady && clockHealthy && !exhausted;
        private NativeControlError Guard(ulong expectedGeneration)
        {
            if (!contextValid) return NativeControlError.StaleIdentity;
            if (exhausted) return NativeControlError.GenerationExhausted;
            if (!Available) return NativeControlError.Unavailable;
            return expectedGeneration == 0 || expectedGeneration != generation ? NativeControlError.StaleGeneration : NativeControlError.None;
        }

        private NativeControlError CheckLease(ulong expectedGeneration, string leaseId, string controllerSessionId)
        {
            var error = Guard(expectedGeneration);
            if (error != NativeControlError.None) return error;
            if (lease == null) return NativeControlError.AuthorityRequired;
            if (lease.ControllerSessionId != controllerSessionId) return NativeControlError.OwnerConflict;
            return lease.LeaseId == leaseId ? NativeControlError.None : NativeControlError.LeaseMismatch;
        }

        private bool Advance()
        {
            if (generation == ulong.MaxValue)
            {
                exhausted = true;
                lease = null;
                reason = NativeControlRevocationReason.GenerationExhausted;
                return false;
            }
            generation = checked(generation + 1);
            return true;
        }

        private void Invalidate(NativeControlRevocationReason revokeReason)
        {
            lease = null;
            deadline = 0;
            if (Advance()) reason = revokeReason;
        }

        private static bool ValidDuration(int leaseMs) => leaseMs >= 1000 && leaseMs <= 30000;
        private bool TryDeadline(int leaseMs, out long expires)
        {
            expires = 0;
            if (now > long.MaxValue - leaseMs)
            {
                Invalidate(NativeControlRevocationReason.ClockUnavailable);
                clockHealthy = false;
                return false;
            }
            expires = checked(now + leaseMs);
            return true;
        }
        private NativeControlSnapshot Snapshot() => new NativeControlSnapshot(contextValid ? identity : null, generation,
            Available, lease, lease == null ? 0 : (int)Math.Min(30000, Math.Max(0, deadline - now)), reason);
        private NativeControlResult Result(NativeControlError error) => new NativeControlResult(error, Snapshot());
    }
}
