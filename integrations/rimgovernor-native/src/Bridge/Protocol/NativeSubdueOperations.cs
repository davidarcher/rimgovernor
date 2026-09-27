#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // The subdue rules of MeleeIntent (subdue = true). Ordinary melee damage
    // applies. This is containment, never execution or custody.
    internal static class NativeSubdueOperations
    {
        internal static Common.Failure? Resolve(string pawnId, string targetId, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Pawn? target, out NativePawnSnapshot? snapshot)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            target = null; snapshot = null;
            pawn = identity.Map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == pawnId);
            target = identity.Map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == targetId);
            if (pawn == null || target == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Subdue pawns are not spawned.");
            var check = NativePawnControlState.Observe(identity, pawn, out snapshot);
            if (check != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(check, context);
            string? reason = null;
            if (!snapshot.Eligible || !pawn.IsColonistPlayerControlled || pawn.WorkTagIsDisabled(WorkTags.Violent)) reason = "incapable colonist";
            else if (snapshot.Drafted && snapshot.Claim == null) reason = "unowned draft";
            else if (pawn.equipment?.Primary != null && !pawn.equipment.Primary.def.IsMeleeWeapon) reason = "ranged weapons are refused";
            else if (target.Faction != Faction.OfPlayer || !target.RaceProps.Humanlike || target.IsPrisonerOfColony || target.Dead || target.Downed || !target.InAggroMentalState) reason = "target must be a standing aggressive colonist";
            else if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) reason = "target is unreachable";
            return reason == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Subdue refused: " + reason);
        }
        // Drafts an undrafted subduer under an owned claim; call under authority.
        internal static NativePawnSnapshot EnsureDrafted(NativeControlIdentity identity, Pawn pawn, NativePawnSnapshot before)
        {
            if (before.Drafted) return before;
            if (NativePawnControlState.PrepareClaim(identity, pawn, before.Token, out var ticket, out _) != NativePawnControlResult.Ready) throw new InvalidOperationException("Draft preparation changed.");
            pawn.drafter.Drafted = true;
            if (NativePawnControlState.CompleteClaim(ticket!, out var after) != NativePawnControlResult.Ready || after?.Claim == null) throw new InvalidOperationException("Draft claim unverified.");
            return after;
        }
        // A containment melee job: a legal native blunt attack (including
        // fists) when available, never killing the downed victim.
        // JobDriver_AttackMelee passes this verb to TryMeleeAttack unchanged.
        internal static Job MakeJob(Pawn pawn, Pawn victim)
        {
            var job = JobMaker.MakeJob(JobDefOf.AttackMelee, victim); job.killIncappedTarget = false;
            job.verbToUse = pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false)
                .Select(v => v.verb).FirstOrDefault(v => v.IsUsableOn(victim) && v.verbProps.meleeDamageDef == DamageDefOf.Blunt);
            return job;
        }
        // Orders the job; it ends once the victim is downed or out of the break.
        internal static bool Take(Pawn pawn, Job job, Pawn victim)
        {
            bool accepted = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (accepted && pawn.CurJob == job) pawn.jobs.curDriver.AddEndCondition(() => victim.Dead || victim.Destroyed ? JobCondition.Incompletable
                : victim.Downed || !victim.InAggroMentalState ? JobCondition.Succeeded : JobCondition.Ongoing);
            return accepted;
        }
    }
}
