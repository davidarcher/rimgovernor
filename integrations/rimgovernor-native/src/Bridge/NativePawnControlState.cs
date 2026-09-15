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
    internal enum NativePawnControlResult { Ready, AlreadyReleased, Unavailable, StaleIdentity, StaleSnapshot, ClaimMismatch, Ineligible, AuthorityRequired, Uncertain, CapacityExhausted }
    internal sealed class NativeDraftClaim
    {
        internal NativeDraftClaim(string id) { ClaimId = id; }
        internal string ClaimId { get; }
    }
    internal sealed class NativePawnSnapshot
    {
        internal NativePawnSnapshot(string token, NativePawnFacts facts, NativeDraftClaim? claim)
        { Token = token; Facts = facts; Claim = claim == null ? null : new NativeDraftClaim(claim.ClaimId); }
        internal string Token { get; }
        internal string PawnId => Facts.PawnId;
        internal bool Drafted => Facts.Drafted;
        internal bool Eligible => Facts.Eligible;
        internal NativeDraftClaim? Claim { get; }
        internal NativePawnFacts Facts { get; }
    }
    internal sealed class NativeDraftClaimTicket
    {
        internal NativeDraftClaimTicket(NativePawnControlRecord record, NativePawnSnapshot before)
        { Record = record; Before = before; }
        internal readonly NativePawnControlRecord Record;
        internal NativePawnSnapshot Before { get; }
        internal bool Used;
    }
    internal sealed class NativeDraftReleaseTicket
    {
        internal NativeDraftReleaseTicket(NativePawnControlRecord record, NativePawnSnapshot before, string claimId)
        { Record = record; Before = before; ClaimId = claimId; }
        internal readonly NativePawnControlRecord Record;
        internal NativePawnSnapshot Before { get; }
        internal string ClaimId { get; }
        internal NativePawnSnapshot? After { get; set; }
    }

    internal sealed class NativePawnJobFacts
    {
        private readonly Job? job;
        private readonly JobDef? definition;
        private readonly LocalTargetInfo a, b, c;
        private readonly LocalTargetInfo[] targetsA, targetsB;
        private readonly bool forced;
        private readonly int count, loadId;
        internal NativePawnJobFacts(Job? value)
        {
            job = value; definition = value?.def; forced = value?.playerForced ?? false;
            a = value?.targetA ?? LocalTargetInfo.Invalid; b = value?.targetB ?? LocalTargetInfo.Invalid; c = value?.targetC ?? LocalTargetInfo.Invalid;
            targetsA = value?.targetQueueA?.ToArray() ?? Array.Empty<LocalTargetInfo>();
            targetsB = value?.targetQueueB?.ToArray() ?? Array.Empty<LocalTargetInfo>();
            count = value?.count ?? 0; loadId = value?.loadID ?? 0;
            if (targetsA.Length > 256 || targetsB.Length > 256) throw new InvalidOperationException("Pawn job target collection exceeds bound.");
        }
        internal bool Same(NativePawnJobFacts other) => ReferenceEquals(job, other.job) && ReferenceEquals(definition, other.definition)
            && forced == other.forced && count == other.count && loadId == other.loadId && a == other.a && b == other.b && c == other.c
            && targetsA.SequenceEqual(other.targetsA) && targetsB.SequenceEqual(other.targetsB);
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
        internal NativePawnJobFacts Job = null!;
        internal NativePawnJobFacts[] Queue = Array.Empty<NativePawnJobFacts>();
        internal bool Eligible => Drafter != null && Spawned && !Dead && !Downed && !Mental && PlayerControlled;
        internal bool SameIdentity(NativePawnFacts other) => ReferenceEquals(Pawn, other.Pawn) && PawnId == other.PawnId
            && SameIdentity(Identity, other.Identity) && ContextRevision == other.ContextRevision;
        internal static bool SameIdentity(NativeControlIdentity a, NativeControlIdentity b) => ReferenceEquals(a.Game, b.Game)
            && ReferenceEquals(a.Map, b.Map) && a.MapId == b.MapId && a.ColonyId == b.ColonyId && a.LoadToken == b.LoadToken;
        internal bool SameEligibility(NativePawnFacts other) => SameIdentity(other) && ReferenceEquals(Drafter, other.Drafter)
            && ReferenceEquals(Faction, other.Faction) && Position == other.Position && Dead == other.Dead && Downed == other.Downed
            && Spawned == other.Spawned && PlayerControlled == other.PlayerControlled && Mental == other.Mental && ReferenceEquals(MentalState, other.MentalState);
        internal bool Same(NativePawnFacts other) => SameEligibility(other) && Drafted == other.Drafted && DraftRevision == other.DraftRevision
            && OrderRevision == other.OrderRevision && Job.Same(other.Job) && Queue.Length == other.Queue.Length
            && Queue.Zip(other.Queue, (a,b) => a.Same(b)).All(value => value);
    }

    // This record owns only one current pawn snapshot/claim and one latest cleanup.
    // Cleanup storage is deliberately independent from the ordinary operation ledger.
    internal sealed class NativePawnControlRecord
    {
        internal NativePawnSnapshot? Snapshot;
        internal NativeDraftClaim? Claim;
        internal ulong ClaimRevision;
        internal NativeDraftReleaseTicket? Release;
        internal ulong OrderRevision;
        internal NativePawnSnapshot Observe(NativePawnFacts facts)
        {
            if (Claim != null && (Snapshot == null || !facts.SameIdentity(Snapshot.Facts) || facts.Drafter == null || !ReferenceEquals(facts.Drafter, Snapshot.Facts.Drafter) || facts.DraftRevision != ClaimRevision || !facts.Drafted)) Claim = null;
            if (Snapshot == null || !facts.Same(Snapshot.Facts) || Snapshot.Claim?.ClaimId != Claim?.ClaimId)
                Snapshot = new NativePawnSnapshot(Guid.NewGuid().ToString("N"), facts, Claim);
            return Snapshot;
        }
        internal NativePawnControlResult PrepareClaim(NativePawnSnapshot current, string token, out NativeDraftClaimTicket? ticket)
        {
            ticket = null;
            if (current.Token != token) return NativePawnControlResult.StaleSnapshot;
            if (current.Drafted || !current.Eligible || current.Claim != null) return NativePawnControlResult.Ineligible;
            ticket = new NativeDraftClaimTicket(this, current);
            return NativePawnControlResult.Ready;
        }
        internal NativePawnControlResult CompleteClaim(NativeDraftClaimTicket ticket, NativePawnFacts facts, out NativePawnSnapshot snapshot)
        {
            snapshot = Observe(facts);
            if (ticket.Used || ticket.Record != this || Claim != null) return NativePawnControlResult.ClaimMismatch;
            ticket.Used = true;
            var before = ticket.Before.Facts;
            if (!facts.SameEligibility(before) || !facts.Eligible || !facts.Drafted || before.Drafted
                || before.DraftRevision == ulong.MaxValue || facts.DraftRevision != before.DraftRevision + 1 || facts.OrderRevision != before.OrderRevision)
                return NativePawnControlResult.Uncertain;
            Claim = new NativeDraftClaim(Guid.NewGuid().ToString("N")); ClaimRevision = facts.DraftRevision;
            Release = null;
            snapshot = Observe(facts);
            return NativePawnControlResult.Ready;
        }
        internal NativePawnControlResult PrepareRelease(NativePawnSnapshot current, string token, string claimId, out NativeDraftReleaseTicket? ticket)
        {
            ticket = null;
            if (Release != null && Release.Before.Token == token && Release.ClaimId == claimId)
            {
                ticket = Release;
                if (!current.Facts.SameIdentity(Release.Before.Facts)) return NativePawnControlResult.StaleIdentity;
                if (current.Facts.OrderRevision != Release.Before.Facts.OrderRevision) return NativePawnControlResult.StaleSnapshot;
                if (Release.After != null && current.Facts.Same(Release.After.Facts) && current.Claim == null) return NativePawnControlResult.AlreadyReleased;
                if (Release.After == null) return current.Facts.DraftRevision <= Release.Before.Facts.DraftRevision + 1
                    ? NativePawnControlResult.Uncertain : NativePawnControlResult.StaleSnapshot;
                return NativePawnControlResult.StaleSnapshot;
            }
            if (current.Token != token) return NativePawnControlResult.StaleSnapshot;
            if (!current.Drafted || current.Claim == null || current.Claim.ClaimId != claimId) return NativePawnControlResult.ClaimMismatch;
            ticket = new NativeDraftReleaseTicket(this, current, claimId);
            Release = ticket; // Admission is correlated even when the caller's native setter throws.
            return NativePawnControlResult.Ready;
        }
        internal NativePawnControlResult CompleteRelease(NativeDraftReleaseTicket ticket, NativePawnFacts facts, out NativePawnSnapshot snapshot)
        {
            snapshot = Observe(facts);
            if (!ReferenceEquals(Release, ticket) || ticket.Record != this || ticket.After != null) return NativePawnControlResult.ClaimMismatch;
            var before = ticket.Before.Facts;
            if (!facts.SameEligibility(before) || facts.Drafted
                || before.DraftRevision == ulong.MaxValue || facts.DraftRevision != before.DraftRevision + 1 || facts.OrderRevision != before.OrderRevision)
                return NativePawnControlResult.Uncertain;
            Claim = null; snapshot = Observe(facts); ticket.After = snapshot;
            return NativePawnControlResult.Ready;
        }
        internal void Ordered(bool causallyOwned)
        {
            if (OrderRevision == ulong.MaxValue) throw new InvalidOperationException("Pawn order revision exhausted.");
            OrderRevision++;
            if (!causallyOwned) Claim = null;
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
        internal static bool IsReady
        {
            get
            {
                if (!initialized) return false;
                try { return DraftOwnership.Healthy && NativeAuthorityHooks.Health.Ready && Targets.Count == 3 && Targets.All(target => {
                    var patch = Harmony.GetPatchInfo(target.Item1);
                    return patch != null && (target.Item2 == null || patch.Prefixes.Any(p => p.owner == HookOwner && p.PatchMethod == target.Item2))
                        && (target.Item3 == null || patch.Postfixes.Any(p => p.owner == HookOwner && p.PatchMethod == target.Item3)); }); }
                catch { return false; }
            }
        }
        private static void Ordered(Pawn ___pawn, bool __result)
        {
            if (!__result || Current.Game == null || !Games.TryGetValue(Current.Game, out var game) || !game.Pawns.TryGetValue(___pawn, out var record)) return;
            bool causallyOwned = record.Claim != null && CausallyOwned(Current.Game);
            try { record.Ordered(causallyOwned); } catch { game.Exhausted = true; }
        }
        private static void BeforeGame(out Game? __state) => __state = Current.Game;
        private static void AfterGame(Game? __state) { if (!ReferenceEquals(__state,Current.Game)) Invalidate(__state); }
        private static void BeforeMap(Game __instance, out Map? __state) => __state = __instance.CurrentMap;
        private static void AfterMap(Game __instance, Map? __state) { if (!ReferenceEquals(__state,__instance.CurrentMap)) Invalidate(__instance); }
        private static void Invalidate(Game? game)
        {
            if (game == null || !Games.TryGetValue(game, out var state)) return;
            if (Interlocked.Increment(ref state.ContextRevision) <= 0) state.Exhausted = true;
        }
        // A claim survives an ordered-job interruption only if it was created,
        // and still lives, within the same causally-owned authority generation
        // scope: there being exactly one bot actor, generation continuity alone
        // proves the claim is still the bot's (see NativeControlAuthority.IsCausalOwnedScope).
        private static bool CausallyOwned(Game game) =>
            NativeControlAuthority.TryGetForGame(game, out var authority) && authority != null && authority.IsCausalOwnedScope();
        // Claim creation additionally requires the authority to be presently
        // Active (Mode.Auto); release does not require this (matching the
        // original asymmetry, which allowed cleanup after Manual/expiry).
        private static bool Active(Game game)
        {
            if (!NativeControlAuthority.TryGetForGame(game, out var authority) || authority == null) return false;
            var status = authority.Status();
            return status.Available && status.Active;
        }
        private static NativePawnControlResult Read(NativeControlIdentity identity, Pawn pawn, out NativePawnControlRecord? record, out NativePawnSnapshot? snapshot)
        {
            record = null; snapshot = null;
            if (identity == null || pawn == null) return NativePawnControlResult.StaleIdentity;
            if (!UnityData.IsInMainThread || !IsReady) return NativePawnControlResult.Unavailable;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return NativePawnControlResult.Unavailable;
            var status = authority.Status();
            if (status.Identity == null || !NativePawnFacts.SameIdentity(identity,status.Identity) || pawn.Map != identity.Map) return NativePawnControlResult.StaleIdentity;
            try
            {
                var game = Games.GetOrCreateValue(identity.Game);
                if (game.Exhausted) return NativePawnControlResult.Unavailable;
                if (!game.Pawns.TryGetValue(pawn,out record))
                {
                    if (game.Pawns.Count >= 4096) return NativePawnControlResult.CapacityExhausted;
                    record = new NativePawnControlRecord(); game.Pawns.Add(pawn,record);
                }
                var revision = pawn.drafter == null ? (ulong?)0 : DraftOwnership.Revision(pawn);
                if (!revision.HasValue || pawn.jobs == null || pawn.jobs.jobQueue == null || pawn.jobs.jobQueue.Count > 256) return NativePawnControlResult.Unavailable;
                var facts = new NativePawnFacts { Identity = identity, Pawn = pawn, PawnId = pawn.GetUniqueLoadID(), Drafter = pawn.drafter,
                    Faction = pawn.Faction, Position = pawn.Position, Drafted = pawn.drafter?.Drafted == true, Dead = pawn.Dead, Downed = pawn.Downed,
                    Spawned = pawn.Spawned, PlayerControlled = pawn.IsColonistPlayerControlled, Mental = pawn.InMentalState, MentalState = pawn.MentalState,
                    DraftRevision = revision.Value, OrderRevision = record.OrderRevision, ContextRevision = checked((ulong)Volatile.Read(ref game.ContextRevision)),
                    Job = new NativePawnJobFacts(pawn.CurJob), Queue = pawn.jobs.jobQueue.Select(q => new NativePawnJobFacts(q.job)).ToArray() };
                if (!ProtoBoundary.IsIdentifier(facts.PawnId)) return NativePawnControlResult.Unavailable;
                snapshot = record.Observe(facts);
                return NativePawnControlResult.Ready;
            }
            catch { return NativePawnControlResult.Unavailable; }
        }
        internal static NativePawnControlResult Observe(NativeControlIdentity identity, Pawn pawn, out NativePawnSnapshot? snapshot) => Read(identity,pawn,out _,out snapshot);
        internal static NativePawnControlResult Check(NativeControlIdentity identity, Pawn pawn, string token, out NativePawnSnapshot? snapshot)
        { var result = Read(identity,pawn,out _,out snapshot); return result != NativePawnControlResult.Ready ? result : snapshot!.Token == token ? result : NativePawnControlResult.StaleSnapshot; }
        internal static NativePawnControlResult PrepareClaim(NativeControlIdentity identity, Pawn pawn, string expectedToken,
            out NativeDraftClaimTicket? ticket, out NativePawnSnapshot? snapshot)
        {
            ticket = null; var result = Read(identity,pawn,out var record,out snapshot);
            if (result != NativePawnControlResult.Ready) return result;
            if (!Active(identity.Game)) return NativePawnControlResult.AuthorityRequired;
            return record!.PrepareClaim(snapshot!,expectedToken,out ticket);
        }
        internal static NativePawnControlResult CompleteClaim(NativeDraftClaimTicket ticket, out NativePawnSnapshot? snapshot)
        {
            var result = Read(ticket.Before.Facts.Identity,ticket.Before.Facts.Pawn,out var record,out snapshot);
            if (result != NativePawnControlResult.Ready) return result;
            if (record != ticket.Record) return NativePawnControlResult.ClaimMismatch;
            return record!.CompleteClaim(ticket,snapshot!.Facts,out snapshot);
        }
        internal static NativePawnControlResult PrepareRelease(NativeControlIdentity identity, Pawn pawn, string expectedToken,string claimId,
            out NativeDraftReleaseTicket? ticket,out NativePawnSnapshot? snapshot)
        {
            ticket = null; var result = Read(identity,pawn,out var record,out snapshot);
            if (result != NativePawnControlResult.Ready) return result;
            if (!ProtoBoundary.IsIdentifier(claimId)) return NativePawnControlResult.ClaimMismatch;
            return record!.PrepareRelease(snapshot!,expectedToken,claimId,out ticket);
        }
        internal static NativePawnControlResult CompleteRelease(NativeDraftReleaseTicket ticket,out NativePawnSnapshot? snapshot)
        {
            var result = Read(ticket.Before.Facts.Identity,ticket.Before.Facts.Pawn,out var record,out snapshot);
            if (result != NativePawnControlResult.Ready) return result;
            if (record != ticket.Record) return NativePawnControlResult.ClaimMismatch;
            return record!.CompleteRelease(ticket,snapshot!.Facts,out snapshot);
        }
    }
}
