namespace RimGovernor.Brain.Policy;

/// <summary>Why a peg-leg step is queued, in selection order; None is a plain harvest or part recovery.</summary>
public enum PegCycleStep
{
    None = 0,
    Reinstall,
    Control,
}

// Peg-leg cycling: medical/surgery installs and removes cheap wood parts (peg leg, wooden hand, wooden foot: one
// pool) on colony prisoners, one surgery at a time in the organ harvest's slot, for prisoner control and release.
// Doctor training is not part of it: the coupled selector's practice surgery (PracticeSurgeryRules, #3637) is that
// path. The constants below are its prices.
public static class DoctorTrainingRules
{
    /// <summary>One colonist hour (2500 ticks) of doctor or warden work.</summary>
    public const double SilverPerLaborHour = 10.0;

    private const double WoodRemovalWork = 2000.0;
    private const double PrisonerControlValue = 200.0;
    private const double WithdrawalControlValue = 400.0;
    private const double WardenFeedCost = 25.0;
    private const double LaborTicksPerHour = GameTime.TicksPerHour;

    private static MaterialInstall? LegInstall(RecipeFacts facts) => facts.MaterialInstallFor("Leg");

    private static bool IsMaterialPart(RecipeFacts facts, string hediff) => facts.MaterialInstallOfPart(hediff) is not null;

    /// <summary>Reports whether recipe is the material install that serves the body part.</summary>
    private static bool MaterialInstallRecipe(RecipeFacts facts, string body, string recipe) =>
        facts.MaterialInstallFor(body) is { } m && m.Recipe == recipe;

    /// <summary>Reports whether the hediff is a material part serving a leg.</summary>
    private static bool LegPart(RecipeFacts facts, string hediff) =>
        facts.MaterialInstallOfPart(hediff) is { } m && m.Bodies.Contains("Leg");

    /// <summary>
    /// Picks at most one peg-leg step under the harvest's in-flight rule, ranked by BetterHarvest: a reinstall
    /// first, then control; then the best gain less cost, then prisoner id. Unknown facts refuse.
    /// </summary>
    public static OrganHarvest? SelectPegCycle(
        Fact<EquatableArray<PrisonerFacts>> prisoners,
        Fact<PrisonerColony> colony,
        Fact<double> food,
        PrisonerPolicy p,
        RecipeFacts facts,
        IReadOnlyDictionary<PawnId, bool>? inFlight)
    {
        if (!prisoners.TryGetValue(out var rows) || !colony.TryGetValue(out var c) || c.Preferences.Forbids(ForbiddenMethod.OrganHarvest) || OrganHarvestRules.SurgeryInFlight(rows, inFlight))
        {
            return null;
        }
        OrganHarvest? best = null;
        foreach (var row in rows)
        {
            foreach (var step in PegCycleSteps(row, c, food, p, facts))
            {
                if (best is null || OrganHarvestRules.BetterHarvest(step, best))
                {
                    best = step;
                }
            }
        }
        return best;
    }

    /// <summary>Reports whether a peg-leg step would be queued now; it keeps the medical/surgery Need unmet.</summary>
    public static bool PegCycleWanted(RoundsFacts f, PrisonerPolicy p) =>
        SelectPegCycle(f.Prisoners, f.PrisonerColony, f.FoodDays, p, f.Recipes, null) is not null;

    /// <summary>Every leg of a human body (two) is missing.</summary>
    internal static bool Legless(this PrisonerFacts row)
    {
        var missing = row.MissingParts.TryGetValue(out var m) ? m : EquatableArray.From(new List<MissingPart>());
        return CountMissing(missing, "Leg") >= 2;
    }

    private static int CountMissing(IEnumerable<MissingPart> missing, string part)
    {
        var n = 0;
        foreach (var m in missing)
        {
            if (m.PartDefName.TryGetValue(out var name) && name == part)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The row's acceptable steps: a reinstall and a control removal, each only when it applies and pays off.</summary>
    private static List<OrganHarvest> PegCycleSteps(PrisonerFacts row, PrisonerColony c, Fact<double> food, PrisonerPolicy p, RecipeFacts facts)
    {
        var (intent, unknown) = PopulationRules.PrisonerIntent(row, c, food, p);
        var dk = row.Dead.TryGetValue(out var dead);
        var ok = row.Operations.TryGetValue(out var ops);
        var mk = row.MissingParts.TryGetValue(out var missing);
        var gk = row.HarvestGoodwill.TryGetValue(out var goodwill);
        var output = new List<OrganHarvest>();
        if (unknown || !dk || dead || !ok || !mk || !gk)
        {
            return output;
        }
        row.CurrentInteraction.TryGetValue(out var current);
        var planned = false;
        foreach (var mode in new[] { intent, current })
        {
            if (mode is PrisonerInteractionMode.Recruit or PrisonerInteractionMode.Convert or PrisonerInteractionMode.Enslave)
            {
                planned = true;
            }
        }
        row.HarvestEligible(c).TryGetValue(out var eligible);
        row.Withdrawal.TryGetValue(out var withdrawal);
        var controlled = !planned && ((eligible && intent != PrisonerInteractionMode.Release) || withdrawal);
        var goodwillCost = OrganHarvestRules.SilverPerGoodwillPoint * Math.Max(0, -goodwill);
        var legsMissing = CountMissing(missing!, "Leg");

        var removals = new List<SurgeryOperation>();
        var installs = new List<SurgeryOperation>();
        var pegLegs = 0;
        foreach (var op in ops!)
        {
            op.PartDefName.TryGetValue(out var body);
            op.Recipe.TryGetValue(out var recipe);
            var ak = op.AddedPart.TryGetValue(out var added);
            var sk = op.IngredientsOnMap.TryGetValue(out var stocked);
            if (!OrganHarvestRules.HarvestAcceptable(op))
            {
                continue;
            }
            if (op.Kind == SurgeryKind.Amputate && ak && IsMaterialPart(facts, added!))
            {
                removals.Add(op);
                if (LegPart(facts, added!))
                {
                    pegLegs++;
                }
            }
            else if (op.Kind == SurgeryKind.Restore && MaterialInstallRecipe(facts, body ?? "", recipe ?? "") && sk && stocked)
            {
                installs.Add(op);
            }
        }

        void Step(PegCycleStep kind, SurgeryOperation op, double gain, double cost)
        {
            op.Recipe.TryGetValue(out var recipe);
            op.PartIndex.TryGetValue(out var part);
            op.Violation.TryGetValue(out var violation);
            output.Add(new OrganHarvest
            {
                Prisoner = (Domain.PawnId)row.Pawn,
                Recipe = recipe ?? "",
                Part = part,
                Gain = gain,
                Cost = cost,
                Violation = violation,
                Step = kind,
            });
        }

        if (legsMissing >= 2 && !controlled)
        {
            foreach (var op in installs)
            {
                if (op.PartDefName.TryGetValue(out var body) && body == "Leg")
                {
                    Step(PegCycleStep.Reinstall, op, 0, 0);
                    break;
                }
            }
        }
        if (controlled && pegLegs > 0 && legsMissing + pegLegs >= 2)
        {
            var value = withdrawal ? WithdrawalControlValue : PrisonerControlValue;
            foreach (var op in removals)
            {
                op.AddedPart.TryGetValue(out var added);
                if (!LegPart(facts, added ?? ""))
                {
                    continue;
                }
                var unit = MedicineUnit(op);
                var removal = unit + goodwillCost + LaborCost(WoodRemovalWork);
                var leg = LegInstall(facts) ?? new MaterialInstall();
                var reinstall = 2 * unit + leg.Value + LaborCost(leg.Work);
                var cost = pegLegs * removal + WardenFeedCost + 2 * reinstall;
                if (value > cost)
                {
                    Step(PegCycleStep.Control, op, value, cost);
                }
                break;
            }
        }
        return output;
    }

    /// <summary>One medicine's value from a removal's medicine value. Unknown reads as none.</summary>
    private static double MedicineUnit(SurgeryOperation op) =>
        op.MedicineValue.TryGetValue(out var v) && double.IsFinite(v) && v >= 0 ? v : 0;

    private static double LaborCost(double work) => SilverPerLaborHour * work / LaborTicksPerHour;
}
