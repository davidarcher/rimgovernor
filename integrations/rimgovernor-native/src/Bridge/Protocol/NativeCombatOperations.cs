#nullable enable
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // The attack rules of a combat order's attack (#939): melee or ranged is
    // the vanilla float menu's choice for the pawn's weapon, and the job is
    // the one the vanilla attack order builds. There is no standalone attack.
    internal static class NativeCombatOperations
    {
        // A pawn's death or a building's destruction; only a pawn is downed.
        internal static bool Dead(Thing thing) => thing is Pawn pawn ? pawn.Dead : thing.Destroyed;
        internal static bool Downed(Thing thing) => thing is Pawn pawn && pawn.Downed;

        internal static void ConfigureRangedJob(Job job, Verb verb, Thing target)
        {
            // Exact ordinary Verse.Verb.OrderForceTarget weapon job shape.
            job.verbToUse = verb; job.targetA = target; job.endIfCantShootInMelee = true;
        }

        internal static bool Legal(Pawn pawn, Thing target, out JobDef? definition, out Verb? verb)
        {
            definition = null; verb = null;
            if (pawn.WorkTagIsDisabled(WorkTags.Violent) || !pawn.Spawned || !target.Spawned || pawn.Map != target.Map || Dead(target) || target.Destroyed) return false;
            if (FloatMenuUtility.UseRangedAttack(pawn))
            {
                if (pawn.equipment?.PrimaryEq?.PrimaryVerb == null
                    || pawn.skills?.GetSkill(SkillDefOf.Shooting)?.TotallyDisabled != false
                    || !pawn.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(target)
                    || FloatMenuUtility.GetRangedAttackAction(pawn, target, out _) == null) return false;
                verb = pawn.equipment.PrimaryEq.PrimaryVerb;
                if (!verb.verbProps.ai_IsWeapon || verb.verbProps.IsMeleeAttack || verb.CasterPawn != pawn || !verb.Available()) return false;
                var minimum = verb.verbProps.EffectiveMinRange(target, pawn);
                if (pawn.Position.DistanceToSquared(target.Position) < minimum * minimum && pawn.Position.AdjacentTo8WayOrInside(target.Position)) return false;
                definition = JobDefOf.AttackStatic;
            }
            else
            {
                if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly) || pawn.meleeVerbs?.TryGetMeleeVerb(target) == null) return false;
                definition = JobDefOf.AttackMelee;
            }
            return true;
        }
    }
}
