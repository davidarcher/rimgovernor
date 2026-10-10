#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal enum NativePawnControlResult { Ready, Unavailable, StaleIdentity, StaleSnapshot, CapacityExhausted }
    internal sealed class NativePawnSnapshot
    {
        internal NativePawnSnapshot(string token, NativePawnFacts facts) { Token = token; Facts = facts; }
        internal string Token { get; }
        internal string PawnId => Facts.PawnId;
        internal bool Drafted => Facts.Drafted;
        internal bool Eligible => Facts.Eligible;
        internal NativePawnFacts Facts { get; }
    }

    internal sealed class NativePawnFacts
    {
        internal NativeControlIdentity Identity = null!;
        internal Pawn Pawn = null!;
        internal string PawnId = "";
        internal Pawn_DraftController? Drafter;
        internal Faction? Faction;
        internal IntVec3 Position;
        internal bool Drafted, Dead, Downed, Spawned, PlayerControlled, Mental;
        internal MentalState? MentalState;
        internal ulong DraftRevision, OrderRevision, ContextRevision;
        internal bool Eligible => Drafter != null && Spawned && !Dead && !Downed && !Mental && PlayerControlled;
        internal bool SameIdentity(NativePawnFacts other) => ReferenceEquals(Pawn, other.Pawn) && PawnId == other.PawnId
            && SameIdentity(Identity, other.Identity) && ContextRevision == other.ContextRevision;
        internal static bool SameIdentity(NativeControlIdentity a, NativeControlIdentity b) => ReferenceEquals(a.Game, b.Game)
            && ReferenceEquals(a.Map, b.Map) && a.MapId == b.MapId && a.ColonyId == b.ColonyId && a.LoadToken == b.LoadToken;
        // Position is observed but never part of the token, and the current
        // job is not observed at all: the clock runs during combat windows
        //, so a walking colonist or a re-issued Wait_Combat would
        // otherwise turn the token over between a read and its order.
        // Orders still bump OrderRevision.
        internal bool Same(NativePawnFacts other) => SameIdentity(other) && ReferenceEquals(Drafter, other.Drafter)
            && ReferenceEquals(Faction, other.Faction) && Dead == other.Dead && Downed == other.Downed
            && Spawned == other.Spawned && PlayerControlled == other.PlayerControlled && Mental == other.Mental && ReferenceEquals(MentalState, other.MentalState)
            && Drafted == other.Drafted && DraftRevision == other.DraftRevision && OrderRevision == other.OrderRevision;
    }

    // One current pawn snapshot. Drafts are plan-owned: native keeps
    // no draft claim; the controller undrafts pawns no live plan needs.
    internal sealed class NativePawnControlRecord
    {
        internal NativePawnSnapshot? Snapshot;
        internal ulong OrderRevision;
        internal NativePawnSnapshot Observe(NativePawnFacts facts)
        {
            if (Snapshot == null || !facts.Same(Snapshot.Facts))
                Snapshot = new NativePawnSnapshot(Guid.NewGuid().ToString("N"), facts);
            return Snapshot;
        }
        internal void Ordered()
        {
            if (OrderRevision == ulong.MaxValue) throw new InvalidOperationException("Pawn order revision exhausted.");
            OrderRevision++;
        }
    }

    internal static class NativePawnControlState
    {
        private const string HookOwner = "rimgovernor.pawn-control";
        private sealed class GameState
        {
            internal readonly Dictionary<Pawn, NativePawnControlRecord> Pawns = new Dictionary<Pawn, NativePawnControlRecord>();
            internal long ContextRevision;
            internal bool Exhausted;
        }
        private static readonly ConditionalWeakTable<Game, GameState> Games = new ConditionalWeakTable<Game, GameState>();
        private static readonly List<Tuple<MethodBase,MethodInfo?,MethodInfo?>> Targets = new List<Tuple<MethodBase,MethodInfo?,MethodInfo?>>();
        private static bool initialized;
        internal static void Initialize()
        {
            if (initialized) return;
            try
            {
                DraftOwnership.Ensure();
                _ = NativeAuthorityHooks.Health; // Eager startup verification; reads never trigger hook installation.
                var harmony = new Harmony(HookOwner);
                Add(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob", new[] { typeof(Job), typeof(JobTag?), typeof(bool) }), null, nameof(Ordered));
                Add(harmony, AccessTools.PropertySetter(typeof(Current), "Game"), nameof(BeforeGame), nameof(AfterGame));
                Add(harmony, AccessTools.PropertySetter(typeof(Game), "CurrentMap"), nameof(BeforeMap), nameof(AfterMap));
                // Under active authority the controller owns every draft
                //: vanilla auto-undraft would drop a pawn a live plan
                // drafted, and the controller's sweep undrafts the rest.
                Add(harmony, AccessTools.Method(typeof(AutoUndrafter), "AutoUndraftTickInterval"), nameof(AutoUndraft), null);
                initialized = true;
            }
            catch { initialized = false; }
        }
        private static void Add(Harmony harmony, MethodBase? target, string? prefix, string? postfix)
        {
            if (target == null) throw new MissingMethodException("Required pawn control hook unavailable.");
            var before = prefix == null ? null : AccessTools.Method(typeof(NativePawnControlState), prefix);
            var after = postfix == null ? null : AccessTools.Method(typeof(NativePawnControlState), postfix);
            harmony.Patch(target, before == null ? null : new HarmonyMethod(before), after == null ? null : new HarmonyMethod(after));
            Targets.Add(Tuple.Create(target,before,after));
        }
        // The hook audit walks Harmony's patch registry, 15-50 ms a pass, and
        // every pawn row asked it (most of a snapshot frame). On the
        // game thread it is re-run at most once per AuditMillis; another mod
        // unpatching a hook is caught within that window. A failed audit is
        // never cached.
        private const long AuditMillis = 1000;
        private static long readyAt;
        private static bool ready;
        internal static bool IsReady
        {
            get
            {
                if (!initialized) return false;
                if (!UnityData.IsInMainThread) return Audit();
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (ready && now - readyAt < AuditMillis * System.Diagnostics.Stopwatch.Frequency / 1000) return ready;
                readyAt = now;
                ready = Audit();
                return ready;
            }
        }
        private static bool Audit()
        {
            try { return DraftOwnership.Healthy && NativeAuthorityHooks.Health.Ready && Targets.Count == 4 && Targets.All(target => {
                    var patch = Harmony.GetPatchInfo(target.Item1);
                    return patch != null && (target.Item2 == null || patch.Prefixes.Any(p => p.owner == HookOwner && p.PatchMethod == target.Item2))
                        && (target.Item3 == null || patch.Postfixes.Any(p => p.owner == HookOwner && p.PatchMethod == target.Item3)); }); }
            catch { return false; }
        }
        private static void Ordered(Pawn ___pawn, bool __result)
        {
            if (!__result || Current.Game == null || !Games.TryGetValue(Current.Game, out var game) || !game.Pawns.TryGetValue(___pawn, out var record)) return;
            try { record.Ordered(); } catch { game.Exhausted = true; }
        }
        private static bool AutoUndraft() => Current.Game == null || !Active(Current.Game);
        private static void BeforeGame(out Game? __state) => __state = Current.Game;
        private static void AfterGame(Game? __state) { if (!ReferenceEquals(__state,Current.Game)) Invalidate(__state); }
        private static void BeforeMap(Game __instance, out Map? __state) => __state = __instance.CurrentMap;
        private static void AfterMap(Game __instance, Map? __state) { if (!ReferenceEquals(__state,__instance.CurrentMap)) Invalidate(__instance); }
        private static void Invalidate(Game? game)
        {
            if (game == null || !Games.TryGetValue(game, out var state)) return;
            if (Interlocked.Increment(ref state.ContextRevision) <= 0) state.Exhausted = true;
        }
        private static bool Active(Game game)
        {
            if (!NativeControlAuthority.TryGetForGame(game, out var authority) || authority == null) return false;
            var status = authority.Status();
            return status.Available && status.Active;
        }
        private static NativePawnControlResult Read(NativeControlIdentity identity, Pawn pawn, out NativePawnSnapshot? snapshot)
        {
            snapshot = null;
            if (identity == null || pawn == null) return NativePawnControlResult.StaleIdentity;
            if (!UnityData.IsInMainThread || !IsReady) return NativePawnControlResult.Unavailable;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return NativePawnControlResult.Unavailable;
            var status = authority.Status();
            if (status.Identity == null || !NativePawnFacts.SameIdentity(identity,status.Identity) || pawn.Map != identity.Map) return NativePawnControlResult.StaleIdentity;
            try
            {
                var game = Games.GetOrCreateValue(identity.Game);
                if (game.Exhausted) return NativePawnControlResult.Unavailable;
                if (!game.Pawns.TryGetValue(pawn,out var record))
                {
                    // Hard count; tracked in #2662 (docs/developers/contracts/kept-constants.md).
                    if (game.Pawns.Count >= 4096) return NativePawnControlResult.CapacityExhausted;
                    record = new NativePawnControlRecord(); game.Pawns.Add(pawn,record);
                }
                var revision = pawn.drafter == null ? (ulong?)0 : DraftOwnership.Revision(pawn);
                if (!revision.HasValue || pawn.jobs == null) return NativePawnControlResult.Unavailable;
                var facts = new NativePawnFacts { Identity = identity, Pawn = pawn, PawnId = pawn.GetUniqueLoadID(), Drafter = pawn.drafter,
                    Faction = pawn.Faction, Position = pawn.Position, Drafted = pawn.drafter?.Drafted == true, Dead = pawn.Dead, Downed = pawn.Downed,
                    Spawned = pawn.Spawned, PlayerControlled = pawn.IsColonistPlayerControlled, Mental = pawn.InMentalState, MentalState = pawn.MentalState,
                    DraftRevision = revision.Value, OrderRevision = record.OrderRevision, ContextRevision = checked((ulong)Volatile.Read(ref game.ContextRevision)) };
                if (!ProtoBoundary.IsIdentifier(facts.PawnId)) return NativePawnControlResult.Unavailable;
                snapshot = record.Observe(facts);
                return NativePawnControlResult.Ready;
            }
            catch { return NativePawnControlResult.Unavailable; }
        }
        internal static NativePawnControlResult Observe(NativeControlIdentity identity, Pawn pawn, out NativePawnSnapshot? snapshot) => Read(identity,pawn,out snapshot);
        internal static NativePawnControlResult Check(NativeControlIdentity identity, Pawn pawn, string token, out NativePawnSnapshot? snapshot)
        { var result = Read(identity,pawn,out snapshot); return result != NativePawnControlResult.Ready ? result : snapshot!.Token == token ? result : NativePawnControlResult.StaleSnapshot; }
    }
}
