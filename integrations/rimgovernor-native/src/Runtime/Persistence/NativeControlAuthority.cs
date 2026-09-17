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
        None, Manual, ExternalOrder, PlayerControl,
        IdentityChanged, Disconnect, Shutdown, HooksUnavailable, GenerationExhausted, ClockUnavailable
    }

    public enum NativeControlError
    {
        None, Unavailable, StaleIdentity, StaleGeneration, AuthorityRequired, GenerationExhausted
    }

    // There is only ever one bot process (see #52): mode replaces the negotiated
    // lease. Auto grants the bot outright; Manual is the local player's own mode.
    public enum NativeControlMode { Auto, Manual }

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

    public sealed class NativeControlSnapshot
    {
        internal NativeControlSnapshot(NativeControlIdentity? identity, ulong generation, bool available,
            bool active, NativeControlRevocationReason reason)
        { Identity = identity; Generation = generation; Available = available; Active = active; Reason = reason; }
        public NativeControlIdentity? Identity { get; }
        public ulong Generation { get; }
        public bool Available { get; }
        public bool Active { get; }
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
    /// Only the explicit player-control adapter may set mode; executors may check.
    /// </summary>
    public sealed class NativeControlAuthority
    {
        private static readonly ConditionalWeakTable<Game, NativeControlAuthority> States = new ConditionalWeakTable<Game, NativeControlAuthority>();
        private readonly Game game;
        private readonly Func<NativeControlIdentity?> context;
        private readonly Func<long> clock;
        private readonly int thread;
        private NativeControlIdentity? identity;
        private bool active;
        private ulong generation;
        private long now;
        private bool observedClock;
        private bool clockHealthy = true;
        private bool hooksReady;
        private bool contextValid;
        private bool contextLost;
        private bool exhausted;
        private int ownedDepth;
        private int pendingContextInvalidation;
        private NativeControlIdentity? ownedIdentity;
        private ulong ownedGeneration;
        private NativeControlRevocationReason reason = NativeControlRevocationReason.HooksUnavailable;

        public static NativeControlAuthority ForGame(Game game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            return States.GetValue(game, value => new NativeControlAuthority(value, Capture, NewClock()));
        }

        // Reads must not create a clock or authority owner merely to report identity.
        public static bool TryGetForGame(Game game, out NativeControlAuthority? authority)
        {
            if (game == null) { authority = null; return false; }
            return States.TryGetValue(game, out authority);
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

        // Game replacement may run on the loading thread. Consume before any CAS,
        // even when an away-and-back transition leaves the final identity unchanged.
        public void RequestContextInvalidation() => Interlocked.Exchange(ref pendingContextInvalidation, 1);

        public NativeControlSnapshot SetHookHealth(bool ready)
        {
            Refresh();
            if (!ready && hooksReady) Invalidate(NativeControlRevocationReason.HooksUnavailable);
            hooksReady = ready;
            return Snapshot();
        }

        // The only way to change authority explicitly: Auto grants the bot
        // authority outright, Manual revokes it. There is no acquire/renew
        // handshake because there is only ever one bot process (see #52).
        public NativeControlResult SetMode(ulong expectedGeneration, NativeControlMode mode)
        {
            Refresh();
            var error = Guard(expectedGeneration);
            if (error != NativeControlError.None) return Result(error);
            if (!Advance()) return Result(NativeControlError.GenerationExhausted);
            if (mode == NativeControlMode.Auto) { active = true; reason = NativeControlRevocationReason.None; }
            else { active = false; reason = NativeControlRevocationReason.Manual; }
            return Result(NativeControlError.None);
        }

        public NativeControlResult Check(ulong expectedGeneration)
        {
            Refresh();
            var error = Guard(expectedGeneration);
            if (error == NativeControlError.None && !active) error = NativeControlError.AuthorityRequired;
            return Result(error);
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
            if (contextValid && !IsOwned && !(revokeReason == NativeControlRevocationReason.ExternalOrder && HasCausalOwnedScope()))
                Invalidate(revokeReason);
            return Snapshot();
        }

        /// <summary>Synchronous admitted work only; never carry this scope across await.</summary>
        public IDisposable Owned()
        {
            Refresh();
            if (!Available || !active) throw new InvalidOperationException("Native authority is not active");
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
                return Available && active && ownedGeneration == generation && CurrentContextMatches();
            }
        }

        /// <summary>
        /// Cleanup attribution only, never write permission. Since there is only
        /// ever one bot process (see #52), causal-scope attribution no longer
        /// needs a caller-supplied owner token: it is proven purely by generation
        /// continuity within the same Owned() scope.
        /// </summary>
        public bool IsCausalOwnedScope()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread || ownedDepth == 0) return false;
            Refresh();
            return HasCausalOwnedScope();
        }

        private bool HasCausalOwnedScope()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread || ownedDepth == 0 || !Available || !CurrentContextMatches()) return false;
            return generation == ownedGeneration;
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
            // One view or game change is one generation: the queued
            // invalidation and the identity comparison below observe the same
            // transition, so the comparison is skipped once the queue fired.
            var queued = Interlocked.Exchange(ref pendingContextInvalidation, 0) != 0;
            if (queued) Invalidate(NativeControlRevocationReason.IdentityChanged);
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
            if (!queued && identity != null && !identity.Same(current!)) Invalidate(NativeControlRevocationReason.IdentityChanged);
            identity = current;
            contextLost = false;
        }

        private bool Available => contextValid && hooksReady && clockHealthy && !exhausted;
        private NativeControlError Guard(ulong expectedGeneration)
        {
            if (!contextValid) return NativeControlError.StaleIdentity;
            if (exhausted) return NativeControlError.GenerationExhausted;
            if (!Available) return NativeControlError.Unavailable;
            return expectedGeneration == 0 || expectedGeneration != generation ? NativeControlError.StaleGeneration : NativeControlError.None;
        }

        private bool Advance()
        {
            if (generation == ulong.MaxValue)
            {
                exhausted = true;
                active = false;
                reason = NativeControlRevocationReason.GenerationExhausted;
                return false;
            }
            generation = checked(generation + 1);
            return true;
        }

        private void Invalidate(NativeControlRevocationReason revokeReason)
        {
            active = false;
            if (Advance()) reason = revokeReason;
        }

        private NativeControlSnapshot Snapshot() => new NativeControlSnapshot(contextValid ? identity : null, generation, Available, active, reason);
        private NativeControlResult Result(NativeControlError error) => new NativeControlResult(error, Snapshot());
    }
}
