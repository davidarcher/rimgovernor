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
        var findings = new List<string>();
        return new() { Known = true, Workers = EquatableArray.From(workers), Surgeries = EquatableArray.From(Surgeries(map, legs, findings)), Findings = EquatableArray.From(findings) };
    }

    private const int SurgeryRows = 64;

    /// <summary>Practice surgeries (#3637): per colony prisoner, each restore on a missing part and each removal of an added part the
    /// native recipe offers now with every other ingredient on the map, per eligible free doctor, per stocked medicine kind the recipe
    /// and the prisoner's care allow. A prisoner with no such step instead offers the steps that make one (#3639): removing a natural
    /// part, or installing an artificial part over it, on the first present natural part of each part def a stocked artificial-part
    /// install applies to. The chance is the vanilla outcome quality with that medicine; the brain picks the legal steps.</summary>
    private static List<Obs.PracticeSurgeryWorker> Surgeries(Map map, NativePathLegs legs, List<string> findings)
    {
        var rows = new List<Obs.PracticeSurgeryWorker>();
        using var scope = new NativePawnDetails.SurgeryScope();
        var facts = NativePawnDetails.SurgeryScope.Facts(map);
        var stock = map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).Where(t => !t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map)).ToList();
        foreach (var patient in map.mapPawns.PrisonersOfColonySpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(patient));
            if (giver == null || patient.Dead) continue;
            var before = rows.Count;
            if (!Rows(patient, giver, false) || rows.Count == before && !Rows(patient, giver, true)) return rows;
        }
        if (rows.Count > 0) findings.Add("practice_surgery_part_fetch_unassessed");
        return rows;

        // Adds the patient's cycle steps, or with slot the steps that make one; false once the read is truncated.
        bool Rows(Pawn patient, WorkGiverDef giver, bool slot)
        {
            var parts = patient.RaceProps.body.AllParts;
            var hediffs = patient.health.hediffSet;
            float? bed = patient.InBed() ? null : facts.BestBed?.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) ?? 1f;
            foreach (var def in patient.def.AllRecipes.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                if (!def.AvailableNow || def.Worker == null || !def.targetsBodyPart || def.surgeryOutcomeEffect == null || !def.Worker.AvailableReport(patient).Accepted) continue;
                var recipe = facts.Recipe(def, map);
                if (!recipe.Others) continue;
                var kinds = stock.Where(t => patient.playerSettings?.medCare.AllowsMedicine(t.def) == true && def.ingredients.Any(i => i.filter.Allows(t)))
                    .Select(t => t.def).Distinct().OrderBy(d => d.defName, StringComparer.Ordinal).ToArray();
                var seen = new HashSet<BodyPartDef>();
                foreach (var part in def.Worker.GetPartsToApplyOn(patient, def).Where(p => def.AvailableOnNow(patient, p)).OrderBy(p => parts.IndexOf(p)))
                {
                    var kind = NativePawnDetails.Kind(patient, def, part);
                    var harvest = false;
                    if (!slot)
                    {
                        if (!(kind == Obs.SurgeryKind.Restore || kind == Obs.SurgeryKind.Amputate && def.Worker is Recipe_RemoveBodyPart
                            && hediffs.hediffs.OfType<Hediff_AddedPart>().Any(h => h.Part == part))) continue;
                    }
                    else
                    {
                        var removes = def.Worker is Recipe_RemoveBodyPart;
                        if (!(removes || def.Worker is Recipe_InstallArtificialBodyPart && kind == Obs.SurgeryKind.Install)
                            || hediffs.PartIsMissing(part) || hediffs.PartOrAnyAncestorHasDirectlyAddedParts(part)
                            || !patient.def.AllRecipes.Any(r => r.Worker is Recipe_InstallArtificialBodyPart && r.AvailableNow
                                && r.appliedOnFixedBodyParts?.Contains(part.def) == true && facts.Recipe(r, map).Others) || !seen.Add(part.def)) continue;
                        // Recipe_RemoveBodyPart.ApplyOnPawn raises the harvest thoughts on a clean part; Recipe_InstallArtificialBodyPart
                        // only on a clean part it violates whose def spawns a thing on removal.
                        harvest = MedicalRecipesUtility.IsClean(patient, part)
                            && (removes || def.Worker.IsViolationOnPawn(patient, part, Faction.OfPlayer) && part.def.spawnThingOnRemoved != null);
                    }
                    foreach (var doctor in recipe.Doctors.Where(d => NativeFoodActivity.Available(d) && d.CanReach(patient, PathEndMode.Touch, Danger.Deadly)))
                    {
                        var work = NativeProductiveSkill.ReadSurgery(doctor, def);
                        var speed = def.workSpeedStat == null ? 1f : doctor.GetStatValue(def.workSpeedStat);
                        var memories = harvest ? EquatableArray.From(HarvestMemories(map, doctor)) : default;
                        foreach (var medicine in kinds)
                        {
                            if (rows.Count >= SurgeryRows) { findings.Add("practice_surgery_read_truncated"); return false; }
                            var count = def.ingredients.Where(i => i.filter.Allows(medicine)).Sum(i => (int)i.CountRequiredOfFor(medicine, def));
                            var source = stock.Where(t => t.def == medicine && doctor.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly))
                                .OrderBy(t => t.Position.DistanceToSquared(doctor.Position)).FirstOrDefault();
                            if (count <= 0 || source == null) continue;
                            var approach = legs.Cost(doctor, doctor.Position, source.Position, PathEndMode.ClosestTouch) is { } fetch
                                && legs.Cost(doctor, source.Position, patient.Position, PathEndMode.Touch) is { } carry ? Fact.Known((double)fetch + carry) : default;
                            rows.Add(new(doctor.GetUniqueLoadID(), patient.GetUniqueLoadID(), def.defName, parts.IndexOf(part), medicine.defName, count,
                                Fact.Known(NativePawnDetails.Chance(def, doctor, patient, part, medicine, bed)), giver.defName, new(patient.Position.x, patient.Position.z),
                                approach, speed > 0 ? Fact.Known((double)def.WorkAmountTotal(null) / speed) : default) { SkillWork = work, Memories = memories });
                        }
                    }
                }
            }
            return true;
        }
    }

    /// <summary>The negative memories a doctor's clean part removal from a colony prisoner gives the free colonists here (#3639), read
    /// unattached off the native path: ThoughtUtility.GiveThoughtsForPawnOrganHarvested records HarvestedOrganFromGuest (and HarvestedOrgan
    /// with Ideology); IdeoUtility.Notify_HistoryEvent hands each to the doctor's self-took and every other member's knows precept comps,
    /// whose own conditions apply here, as does MemoryThoughtHandler.TryGainMemory's ThoughtUtility.CanGetThought (Psychopath, Bloodlust).</summary>
    private static IEnumerable<Obs.PracticeSurgeryMemory> HarvestMemories(Map map, Pawn doctor)
    {
        var events = ModsConfig.IdeologyActive ? new[] { HistoryEventDefOf.HarvestedOrganFromGuest, HistoryEventDefOf.HarvestedOrgan } : new[] { HistoryEventDefOf.HarvestedOrganFromGuest };
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            if (pawn.Ideo == null || pawn.needs?.mood == null) continue;
            foreach (var precept in pawn.Ideo.PreceptsListForReading)
            {
                foreach (var comp in precept.def.comps)
                {
                    ThoughtDef? def = null;
                    if (pawn == doctor && comp is PreceptComp_SelfTookMemoryThought self && events.Contains(self.eventDef) && (!self.onlyForNonSlaves || !pawn.IsSlave)
                        && (self.thought.minExpectation == null || ExpectationsUtility.CurrentExpectationFor(pawn).order >= self.thought.minExpectation.order)) def = self.thought;
                    else if (pawn != doctor && comp is PreceptComp_KnowsMemoryThought knows && events.Contains(knows.eventDef)
                        && (precept.def.enabledForNPCFactions || pawn.CountsAsNonNPCForPrecepts())
                        && (!(knows.doerMustBeMyFaction ?? knows.thought.stages.Any(s => s.baseMoodEffect != 0f)) || doctor.Faction == pawn.Faction)
                        && (!knows.doerMustBeMyIdeo || doctor.Ideo == pawn.Ideo) && (!knows.onlyForNonSlaves || !pawn.IsSlave)) def = knows.thought;
                    if (def == null || !ThoughtUtility.CanGetThought(pawn, def)) continue;
                    var thought = ThoughtMaker.MakeThought(def, precept);
                    thought.pawn = pawn;
                    var offset = thought.MoodOffset();
                    if (!(offset < 0)) continue;
                    yield return new(pawn.GetUniqueLoadID(), new Obs.NeedMemory(def.defName, offset, Fact.Known((double)thought.DurationTicks), def.lerpMoodToZero) { StackLimit = def.stackLimit });
                }
            }
        }
    }

    /// <summary>A practice step is still assigned: a maintenance practice while its role requirement stands, a practice surgery (#3637)
    /// while a fresh read supports its step.</summary>
    internal static bool Assigned(Pawn pawn, PawnActivityStep step) => step.Practice is { Surgery: not null }
        ? NativePracticeSurgery.Supported(pawn.Map, step) : step.Practice is { } practice && RoleAssigned(pawn, practice);

    private static bool RoleAssigned(Pawn pawn, PawnActivityPractice practice)
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
        if (practice.Surgery != null) return new(task.Id, Fact.Known(NativePracticeSurgery.Supported(map, task.Step)), []);
        var beneficiary = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == practice.Beneficiary);
        if (beneficiary == null) return new(task.Id, default, []);
        if (!RoleAssigned(beneficiary, practice)) return new(task.Id, Fact.Known(false), []);
        var role = beneficiary.Ideo.GetRole(beneficiary);
        var requirement = (RoleRequirement_MinSkillAny)role.def.roleRequirements[practice.RequirementIndex];
        return new(task.Id, Fact.Known(true), EquatableArray.From(requirement.skills.Select(s =>
            new Obs.PracticeFloorRead(s.skill.defName, s.minLevel, NativeSkillLearning.Read(beneficiary, beneficiary.skills.GetSkill(s.skill))))));
    }

    internal static bool Legal(Pawn pawn, Thing target, PawnActivityStep step)
    {
        if (step.Practice is { Surgery: not null }) return NativePracticeSurgery.Legal(pawn, target, step);
        if (step.Practice is not { } practice || !RoleAssigned(pawn, practice) || target.IsForbidden(pawn)) return false;
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
