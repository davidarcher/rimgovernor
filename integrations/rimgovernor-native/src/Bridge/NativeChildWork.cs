using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>The native age-dependent work states of a child. Existing non-age disabilities stay observed.</summary>
internal static class NativeChildWork
{
    internal static ChildWorkRead? Read(Pawn pawn)
    {
        if (!pawn.DevelopmentalStage.Child()) return null;
        // These names originate in typed driver/recipe stat references in the catalog; no typed route exists for each recipe's stat.
        var stats = RimGovernor.Brain.Bridge.DefinitionCatalog.UpkeepStatNames(DefDatabase<RecipeDef>.AllDefsListForReading)
            .Where(n => n != nameof(StatDefOf.MoveSpeed) && n != nameof(StatDefOf.CarryingCapacity))
            .Select(n => DefDatabase<StatDef>.GetNamedSilentFail(n) ?? throw new InvalidOperationException("upkeep stat not loaded: " + n)).ToArray();
        var last = Math.Max(20, Math.Max(stats.Select(s => NativeAgeStat.LastAge(pawn, s)).DefaultIfEmpty(0).Max(),
            Math.Max(pawn.RaceProps.lifeStageWorkSettings.Select(s => s.minAge).DefaultIfEmpty(0).Max(),
                (int)Math.Ceiling(pawn.RaceProps.lifeStageAges.Max(s => s.minAge)))));
        var disabled = NonAgeDisabled(pawn);
        var ages = new List<AgeWorkRead>();
        for (var age = pawn.ageTracker.AgeBiologicalYears; age <= last; age++)
        {
            var speeds = stats.Select(s => new WorkSpeedRead(s.defName, NativeAgeStat.Value(pawn, s, age))).ToArray();
            var skillSpeeds = new List<SkillSpeedRead>();
            foreach (var record in pawn.skills.skills.OrderBy(s => s.def.defName, StringComparer.Ordinal))
                foreach (var stat in stats)
                    if (NativeSkillStat.OneLevelUp(pawn, stat, record.def, age) is { } value) skillSpeeds.Add(new(record.def.defName, stat.defName, value));
            ages.Add(new(age, EquatableArray.From(speeds), EquatableArray.From(skillSpeeds), EquatableArray.From(
                disabled.Concat(pawn.RaceProps.lifeStageWorkSettings.Where(s => age < s.minAge).Select(s => s.workType))
                    .Select(w => w.defName).Distinct().Order(StringComparer.Ordinal))));
        }
        return new(pawn.ageTracker.AgeBiologicalTicks / (double)GenDate.TicksPerYear, pawn.ageTracker.ChildAgingMultiplier, pawn.ageTracker.AdultAgingMultiplier,
            pawn.genes?.BiologicalAgeTickFactor ?? 1, EquatableArray.From(ages));
    }

    // Pawn.GetDisabledWorkTypes: permanent restrictions plus each observed non-age source. Subtracting the age-disabled set
    // from the combined list would erase a trait/backstory restriction on the same work type.
    private static HashSet<WorkTypeDef> NonAgeDisabled(Pawn pawn)
    {
        var result = pawn.GetDisabledWorkTypes(permanentOnly: true).ToHashSet();
        if (pawn.IsMutant && pawn.mutant.HasTurned) return result;
        if (pawn.health != null) result.UnionWith(pawn.health.DisabledWorkTypes);
        if (pawn.royalty != null && !pawn.IsSlave)
            foreach (var title in pawn.royalty.AllTitlesForReading.Where(t => t.conceited)) result.UnionWith(title.def.DisabledWorkTypes);
        if (ModsConfig.IdeologyActive && pawn.Ideo?.GetRole(pawn) is { } role) result.UnionWith(role.DisabledWorkTypes);
        if (ModsConfig.BiotechActive && pawn.genes != null)
            foreach (var gene in pawn.genes.GenesListForReading) result.UnionWith(gene.DisabledWorkTypes);
        foreach (var quest in QuestUtility.GetWorkDisabledQuestPart(pawn)) result.UnionWith(quest.DisabledWorkTypes);
        if (pawn.guest != null) result.UnionWith(pawn.guest.GetDisabledWorkTypes());
        return result;
    }
}
