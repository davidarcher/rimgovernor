#nullable enable
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
                        Fact.Known((double)path), Fact.Known(duration), learning)
                    {
                        PreparationTicks = ranged ? 0 : SparringBouts.JoinWindowTicks, CallbackIntervalTicks = Fact.Known(cycle), RawXpPerCallback = Fact.Known(ranged ? (double)RangeTraining.ShotXp(weapon) : 200d * cycle / 60d),
                        Equipment = dummy == null ? [] : [dummy.GetUniqueLoadID()],
                        JoyGainPerTick = ranged || pawn.needs.joy == null ? Fact.Known(0d) : Fact.Known(
                            DefDatabase<JobDef>.GetNamed(SparringBouts.JobName).joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour
                            * pawn.needs.joy.tolerances.JoyFactorFromTolerance(DefDatabase<JobDef>.GetNamed(SparringBouts.JobName).joyKind)),
                        Findings = ["conditional_successful_practice_cycles", "practice_injury_recovery_unassessed", "practice_company_mood_unassessed", "future_tolerance_changes", "practice_movement_repair_overhead_unassessed"]
                    });
                }
            }
        }
        return new() { Known = true, Workers = EquatableArray.From(workers) };
    }

    internal static bool Assigned(Pawn pawn, PawnActivityPractice practice)
    {
        var beneficiary = pawn.Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == practice.Beneficiary);
        var role = beneficiary?.Ideo?.GetRole(beneficiary);
        return role != null && role.GetUniqueLoadID() == practice.Role && role.Active
            && practice.RequirementIndex >= 0 && practice.RequirementIndex < role.def.roleRequirements.Count
            && role.def.roleRequirements[practice.RequirementIndex] is RoleRequirement_MinSkillAny requirement
            && requirement.skills.Any(s => s.skill.defName == practice.Skill);
    }

    internal static Obs.PracticeMandateRead Mandate(Map map, SelectedActivityTask task)
    {
        var practice = task.Step.Practice!;
        var beneficiary = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == practice.Beneficiary);
        if (beneficiary == null) return new(task.Id, default, []);
        if (!Assigned(beneficiary, practice)) return new(task.Id, Fact.Known(false), []);
        var role = beneficiary.Ideo.GetRole(beneficiary);
        var requirement = (RoleRequirement_MinSkillAny)role.def.roleRequirements[practice.RequirementIndex];
        return new(task.Id, Fact.Known(true), EquatableArray.From(requirement.skills.Select(s =>
            new Obs.PracticeFloorRead(s.skill.defName, s.minLevel, NativeSkillLearning.Read(beneficiary, beneficiary.skills.GetSkill(s.skill))))));
    }

    internal static bool Legal(Pawn pawn, Thing target, PawnActivityStep step)
    {
        if (step.Practice is not { } practice || !Assigned(pawn, practice) || target.IsForbidden(pawn)) return false;
        if (practice.Skill == "Shooting") return RangeTraining.Eligible(pawn, SkillDefOf.Shooting)
            && target.def == RangeTraining.StandDef && RangeTraining.DummyFor(target) is { } dummy
            && !dummy.IsForbidden(pawn) && pawn.CanReserve(target) && pawn.CanReserve(dummy);
        return practice.Skill == "Melee" && SparringBouts.Eligible(pawn) && target.def == SparringBouts.MarkerDef && pawn.CanReserve(target);
    }

    internal static Job? Job(Pawn pawn, Thing target, PawnActivityStep step)
    {
        if (!Legal(pawn, target, step)) return null;
        if (step.Practice!.Skill == "Shooting")
        {
            var job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(RangeTraining.ShootingJob), target, RangeTraining.DummyFor(target));
            job.maxNumStaticAttacks = RangeTraining.SessionCycles; return job;
        }
        return NativePawnActivities.TrySelectPracticePair(pawn, target, step)
            ? JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(SparringBouts.JobName), target) : null;
    }
}
