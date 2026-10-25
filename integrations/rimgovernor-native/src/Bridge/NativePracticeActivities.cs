using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Runtime;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
namespace HomeBridge.BridgeTools;

internal static class NativePracticeActivities
{
    /// <summary>Every free colonist's practice at every stand or marker (#3341).</summary>
    internal static Obs.PracticeActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var workers = new List<Obs.PracticeWorker>();
        // Every stand and marker, for a drill's company (#3871).
        var practiceTargets = RangeTraining.Stands(map).Concat(map.listerThings.ThingsOfDef(SparringBouts.MarkerDef)).ToArray();
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            if (!NativeFoodActivity.Available(pawn)) continue;
            foreach (var skill in new[] { SkillDefOf.Shooting, SkillDefOf.Melee })
            {
                var ranged = skill == SkillDefOf.Shooting;
                if (!(ranged ? RangeTraining.Eligible(pawn, skill) : SparringBouts.Eligible(pawn))) continue;
                var weapon = ranged ? RangeTraining.UnlockedWeapon() : SparringRules.UnlockedWeapon();
                if (weapon == null) continue;
                // Native verb/cooldown methods on an unspawned, unequipped object. No ThingMaker,
                // PostMake, equipment swap, skill change or live pawn stat override.
                var probe = new ThingWithComps { def = weapon };
                probe.InitializeComps();
                var verb = probe.GetComp<CompEquippable>()?.PrimaryVerb;
                if (verb == null) continue;
                var cycle = verb.verbProps.AdjustedFullCycleTime(verb, pawn) * 60d;
                if (!double.IsFinite(cycle) || cycle <= 0) continue;
                var learning = NativeSkillLearning.Read(pawn, pawn.skills.GetSkill(skill));
                var duration = ranged ? cycle * RangeTraining.SessionCycles : cycle * SparringStopRule.MaxExchanges + SparringBouts.JoinWindowTicks;
                var targets = ranged ? RangeTraining.Stands(map) : map.listerThings.ThingsOfDef(SparringBouts.MarkerDef);
                foreach (var target in targets.OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                {
                    if (target.IsForbidden(pawn) || !pawn.CanReserveAndReach(target, PathEndMode.OnCell, Danger.None)) continue;
                    var dummy = ranged ? RangeTraining.DummyFor(target) : null;
                    if (ranged && (dummy == null || dummy.IsForbidden(pawn) || !pawn.CanReserve(dummy))) continue;
                    if (legs.Cost(pawn, pawn.Position, target.Position, PathEndMode.OnCell) is not { } path) continue;
                    workers.Add(new(pawn.GetUniqueLoadID(), skill.defName, target.GetUniqueLoadID(), new(target.Position.x, target.Position.z),
                        ranged ? "RimGovernor_TrainRange" : "RimGovernor_Spar", ranged ? RangeTraining.ShootingJob : SparringBouts.JobName,
                        path, duration, learning)
                    {
                        PreparationTicks = ranged ? 0 : SparringBouts.JoinWindowTicks, CallbackIntervalTicks = cycle, RawXpPerCallback = ranged ? (double)RangeTraining.ShotXp(weapon) : 200d * cycle / 60d,
                        Equipment = dummy == null ? [] : [dummy.GetUniqueLoadID()],
                        JoyGainPerTick = ranged || pawn.needs.joy == null ? 0d
                            : DefDatabase<JobDef>.GetNamed(SparringBouts.JobName).joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour
                            * pawn.needs.joy.tolerances.JoyFactorFromTolerance(DefDatabase<JobDef>.GetNamed(SparringBouts.JobName).joyKind),
                        Thoughts = TrainedWith(pawn),
                        Company = ranged ? Company(target, practiceTargets) : [],
                        Findings = ranged
                            ? ["conditional_successful_practice_cycles", "practice_injury_recovery_unassessed", "practice_company_outside_sessions_not_modelled", "future_tolerance_changes", "practice_movement_repair_overhead_unassessed"]
                            : ["conditional_successful_practice_cycles", "practice_injury_recovery_unassessed", "future_tolerance_changes", "practice_movement_repair_overhead_unassessed"]
                    });
                }
            }
        }
        return new() { Workers = EquatableArray.From(workers) };
    }

    /// <summary>
    /// The memories a completed session in company gives the pawn (#3858, #3871): the spar's and the drill's finish actions call
    /// TrainingCompany.Finished, which gains RimGovernor_TrainedWith through MemoryThoughtHandler.TryGainMemory when the session
    /// was shared (a bout) or NearTrainer finds company; that refuses it where ThoughtUtility.CanGetThought does. None without a
    /// mood or the def.
    /// </summary>
    private static EquatableArray<Obs.NeedMemory> TrainedWith(Pawn pawn)
    {
        // The mod's own def has no DefOf; TrainingCompany names it.
        var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(TrainingCompany.ThoughtName);
        return def == null || pawn.needs?.mood == null || !ThoughtUtility.CanGetThought(pawn, def) ? [] : [NativeNeedsForecast.Memory(pawn, def)];
    }

    /// <summary>The other practice targets where a colonist on its session is a drill's company at <paramref name="stand"/>
    /// (TrainingCompany.NearTrainer: SocialInteractionUtility.IsGoodPositionForInteraction between the cells; each session stands
    /// on its target's cell). Company while walking to a target, or in the CompanyTicks after another's session, is the
    /// practice_company_outside_sessions_not_modelled finding.</summary>
    private static EquatableArray<string> Company(Thing stand, Thing[] targets) => EquatableArray.From(targets
        .Where(t => t != stand && SocialInteractionUtility.IsGoodPositionForInteraction(stand.Position, t.Position, stand.Map))
        .Select(t => t.GetUniqueLoadID()).Order(StringComparer.Ordinal));

    /// <summary>The selected practice is still the pawn's to do: it may practise the skill (RangeTraining.CanTrain, under the
    /// ceiling) and the stand or marker is usable. No Ideology role is required (#3813).</summary>
    internal static bool Legal(Pawn pawn, Thing target, PawnActivityStep step)
    {
        if (step.Practice is not { } practice || target.IsForbidden(pawn)) return false;
        if (practice.Skill == "Shooting") return RangeTraining.Eligible(pawn, SkillDefOf.Shooting)
            && target.def == RangeTraining.StandDef && RangeTraining.DummyFor(target) is { } dummy
            && !dummy.IsForbidden(pawn) && pawn.CanReserve(target) && pawn.CanReserve(dummy);
        return practice.Skill == "Melee" && SparringBouts.Eligible(pawn) && target.def == SparringBouts.MarkerDef && pawn.CanReserve(target);
    }

    internal static Job? Job(Pawn pawn, Thing target, PawnActivityStep step)
    {
        if (!Legal(pawn, target, step)) return null;
        if ((step.Practice ?? throw new System.InvalidOperationException("Required native value is unavailable: step.Practice.")).Skill == "Shooting")
        {
            var job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(RangeTraining.ShootingJob), target, RangeTraining.DummyFor(target));
            job.maxNumStaticAttacks = RangeTraining.SessionCycles; return job;
        }
        return NativePawnActivities.TrySelectPracticePair(pawn, target, step)
            ? JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(SparringBouts.JobName), target) : null;
    }
}
