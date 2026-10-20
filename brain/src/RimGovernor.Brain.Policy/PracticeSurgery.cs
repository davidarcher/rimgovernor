using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>A legal practice surgery (#3637): the native row, and for a step that opens a practice cycle on a prisoner without one (#3639)
/// the material install whose cycle it opens.</summary>
internal sealed record LegalPracticeSurgery(PracticeSurgeryWorker Row, MaterialInstall? Slot);

/// <summary>The legality gate of the practice-surgery step (#3637); its candidates are generated with the other practice steps.</summary>
internal static class PracticeSurgeryRules
{
    /// <summary>
    /// The practice surgeries that are legal now (#3637): admitted as practice is; the player allows organ harvest; no prisoner surgery
    /// is queued or in flight; the step is on a living held prisoner with its ingredients on the map; that doctor's native failure chance with
    /// that medicine is at most <see cref="SurgeryRules.RestoreFailureCap"/>; doctor and patient are unclaimed; and its timing and learning are
    /// read. The step is either a practice-cycle step (<see cref="RecipeFacts.PracticeCycleOf"/>) with no violation, or, on a prisoner with no
    /// cycle step at all (#3639), a non-lethal step that opens one (<see cref="RecipeFacts.PracticeSlotOf"/>) on a part a prisoner may lose, a
    /// learning install, and a prisoner the population policy is not recruiting (<see cref="PopulationRules.Recruiting"/>). Legality only:
    /// whether it is worth the doctor's time, the medicine, the prisoner's risk and the colony's mood is the forecast's.
    /// </summary>
    internal static LegalPracticeSurgery[] LegalSurgeries(RoundsFacts facts, PracticeActivityRead read, bool admitted, ISet<string> findings)
    {
        if (read.Surgeries.Count == 0 || !admitted) return [];
        if (facts.Preferences.Forbids(ForbiddenMethod.OrganHarvest)) { findings.Add("practice_surgery_forbidden_by_preference"); return []; }
        if (!facts.Prisoners.TryGetValue(out var rows)) { findings.Add("practice_surgery_prisoners_unknown"); return []; }
        if (OrganHarvestRules.SurgeryInFlight(rows, null)) { findings.Add("practice_surgery_held_surgery_in_flight"); return []; }
        var legal = new List<LegalPracticeSurgery>();
        foreach (var row in read.Surgeries)
        {
            var prisoner = rows.FirstOrDefault(r => r.Pawn.Value == row.Patient);
            if (prisoner is null || prisoner.Prisoner != Known(true) || prisoner.Dead != Known(false) || !prisoner.Operations.TryGetValue(out var ops)) continue;
            var op = ops.FirstOrDefault(o => o.Recipe == Known(row.Recipe) && o.PartIndex == Known(row.Part));
            if (op is null || op.IngredientsOnMap != Known(true) || !row.SuccessChance.TryGetValue(out var chance) || 1 - chance > SurgeryRules.RestoreFailureCap
                || facts.WorkView!.Claims.Pawns.Contains(row.Doctor) || facts.WorkView.Claims.Targets.Contains(row.Patient)
                || row.SkillWork is null || !row.ApproachTicks.IsKnown || !row.WorkTicks.IsKnown || row.WorkTicks.Value <= 0) continue;
            if (facts.Recipes.PracticeCycleOf(op) is not null)
            {
                if (op.Violation == Known(false)) legal.Add(new(row, null));
                continue;
            }
            if (facts.Recipes.PracticeSlotOf(op) is not { Learning: not null } slot || ops.Any(o => facts.Recipes.PracticeCycleOf(o) is not null)
                || op.Lethal != Known(false) || !op.PartDefName.TryGetValue(out var body) || facts.Recipes.KeptPart(body)) continue;
            var recruiting = facts.PrisonerColony.TryGetValue(out var colony) ? PopulationRules.Recruiting(prisoner, colony) : Unknown<bool>();
            if (recruiting != Known(false))
            {
                if (!recruiting.IsKnown) findings.Add("practice_slot_recruiting_unknown");
                continue;
            }
            legal.Add(new(row, slot));
        }
        return legal.Take(32).ToArray();
    }
}
