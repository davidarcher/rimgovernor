using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The lesson mechanics the classroom credit prices (#3809, Obs.SchoolRead): the learning need's rates, the learning desires,
/// the colony's school desks, the teacher <c>SchoolUtility.FindTeacher</c> takes first and every child with a learning need,
/// with the skills that teacher's lessons would teach it. Read-only: no desire, job or skill changes.
/// </summary>
internal static class NativeSchool
{
    internal static Obs.SchoolRead Read(Map map, List<Pawn> people)
    {
        var lesson = LearningDesireDefOf.Lessontaking;
        var teacher = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(CanTeach);
        var count = TopTeachableSkillCount();
        var children = people.Where(p => p.needs?.learning != null).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => new Obs.SchoolChildRead
            {
                Pawn = p.GetUniqueLoadID(), LearningRateFactor = F(p.GetStatValue(StatDefOf.LearningRateFactor), "learning rate factor"),
                Taught = teacher == null ? [] : EquatableArray.From(Taught(teacher, p, count)),
            }).ToArray();
        return new()
        {
            FallPerTick = Need_Learning.BaseFallPerTick, RefillPerTick = LearningUtility.NeedSatisfiedPerTick,
            // Without Biotech no LearningDesireDef is loaded: no lesson, and no child has a learning need.
            Lessontaking = lesson?.defName ?? "", LessonXpPerTick = lesson?.xpPerTick ?? 0,
            Desires = EquatableArray.From(DefDatabase<LearningDesireDef>.AllDefsListForReading
                .Select(d => new Obs.LearningDesireRead(d.defName, F(d.selectionWeight, "learning desire weight"), d.Worker.CanGiveDesire))),
            StandingDesks = ThingDefOf.SchoolDesk == null ? 0 : map.listerThings.ThingsOfDef(ThingDefOf.SchoolDesk).Count(t => t.Spawned && t.Faction == Faction.OfPlayer),
            Teacher = teacher?.GetUniqueLoadID() ?? "",
            Children = EquatableArray.From(children),
        };
    }

    // SchoolUtility.CanTeachNow's standing clauses (ChildcareUtility.SpawnedColonistChildcarers' Childcare too): no juvenile,
    // Childcare neither disabled nor inactive, able to talk. Its passing clauses (asleep, drafted, downed, a basic need soon, a
    // mental state, a lord) change by the hour, so the first standing teacher is the one lessons are priced with.
    private static bool CanTeach(Pawn pawn) => !pawn.DevelopmentalStage.Juvenile() && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Childcare)
        && pawn.workSettings != null && pawn.workSettings.WorkIsActive(WorkTypeDefOf.Childcare) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking);

    // JobDriver_Lessongiving.ChooseSkill: the teacher's skills neither it nor the child is TotallyDisabled in that have a
    // lessonInteraction, best first (SkillRecord.CompareTo), the top TopTeachableSkillCount.
    private static IEnumerable<Obs.TaughtSkillRead> Taught(Pawn teacher, Pawn child, int count)
    {
        var skills = child.skills ?? throw new InvalidOperationException("child without a skill tracker: " + child.GetUniqueLoadID());
        return (teacher.skills?.skills ?? throw new InvalidOperationException("teacher without a skill tracker: " + teacher.GetUniqueLoadID()))
            .Where(s => !s.TotallyDisabled && s.def.lessonInteraction != null && !skills.GetSkill(s.def).TotallyDisabled)
            .OrderByDescending(s => s).Take(count)
            .Select(s => new Obs.TaughtSkillRead(s.def.defName, NativeSkillLearning.Read(child, skills.GetSkill(s.def))))
            .ToArray();
    }

    /// <summary>JobDriver_Lessongiving's private TopTeachableSkillCount. No member exposes it; a version that renames it is a broken read.</summary>
    private static int TopTeachableSkillCount() =>
        BridgeCommon.PrivateStaticField(typeof(JobDriver_Lessongiving), "TopTeachableSkillCount")?.GetRawConstantValue() is int n && n > 0 ? n
            : throw new InvalidOperationException("JobDriver_Lessongiving.TopTeachableSkillCount is not readable.");

    private static double F(double value, string what) => double.IsFinite(value) ? value : throw new InvalidOperationException(what + " is not finite.");
}
