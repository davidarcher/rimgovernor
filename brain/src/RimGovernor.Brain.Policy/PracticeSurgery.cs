using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>The legality gate of the practice-surgery step (#3637); its candidates are generated with the other practice steps.</summary>
internal static class PracticeSurgeryRules
{
    /// <summary>
    /// The practice surgeries that are legal now (#3637): admitted as practice is; the player allows organ harvest; no prisoner surgery
    /// is queued or in flight; the step is a practice-cycle step (<see cref="RecipeFacts.PracticeCycleOf"/>) of a living held prisoner
    /// with its ingredients on the map and no violation; that doctor's native failure chance with that medicine is at most
    /// <see cref="SurgeryRules.RestoreFailureCap"/>; doctor and patient are unclaimed; and its timing and learning are read.
    /// Legality only: whether it is worth the doctor's time and the medicine is the Investment term's.
    /// </summary>
    internal static PracticeSurgeryWorker[] LegalSurgeries(RoundsFacts facts, PracticeActivityRead read, bool admitted, ISet<string> findings)
    {
        if (read.Surgeries.Count == 0 || !admitted) return [];
        if (facts.Preferences.Forbids(ForbiddenMethod.OrganHarvest)) { findings.Add("practice_surgery_forbidden_by_preference"); return []; }
        if (!facts.Prisoners.TryGetValue(out var rows)) { findings.Add("practice_surgery_prisoners_unknown"); return []; }
        if (OrganHarvestRules.SurgeryInFlight(rows, null)) { findings.Add("practice_surgery_held_surgery_in_flight"); return []; }
        return read.Surgeries.Where(row =>
        {
            var prisoner = rows.FirstOrDefault(r => r.Pawn.Value == row.Patient);
            if (prisoner is null || prisoner.Prisoner != Known(true) || prisoner.Dead != Known(false) || !prisoner.Operations.TryGetValue(out var ops)) return false;
            var op = ops.FirstOrDefault(o => o.Recipe == Known(row.Recipe) && o.PartIndex == Known(row.Part));
            return op is not null && facts.Recipes.PracticeCycleOf(op) is not null && op.IngredientsOnMap == Known(true) && op.Violation == Known(false)
                && row.SuccessChance.TryGetValue(out var chance) && 1 - chance <= SurgeryRules.RestoreFailureCap
                && !facts.WorkView!.Claims.Pawns.Contains(row.Doctor) && !facts.WorkView.Claims.Targets.Contains(row.Patient)
                && row.SkillWork is not null && row.ApproachTicks.IsKnown && row.WorkTicks.IsKnown && row.WorkTicks.Value > 0;
        }).Take(32).ToArray();
    }
}
