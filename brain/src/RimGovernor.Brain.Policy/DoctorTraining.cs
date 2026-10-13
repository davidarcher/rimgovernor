namespace RimGovernor.Brain.Policy;

/// <summary>Why a peg-leg step is queued, in selection order; None is a plain harvest or part recovery.</summary>
public enum PegCycleStep
{
    None = 0,
    Reinstall,
    Control,
    Training,
}

// Peg-leg cycling: MaintainSurgery installs and removes cheap wood parts (peg leg, wooden hand, wooden foot: one
// pool) on colony prisoners, one surgery at a time in the organ harvest's slot, for doctor training, prisoner
// control and release. See the Go source for the full model; the constants below are its prices.
public static partial class PolicyFunctions
{
    /// <summary>The Medicine level the colony trains doctors to.</summary>
    public const int DoctorTrainingFloor = 10;

    /// <summary>One colonist hour (2500 ticks) of doctor or warden work.</summary>
    public const double SilverPerLaborHour = 10.0;

    private const double SurgeryXPPerWork = 1.6;
    private const double WoodRemovalWork = 2000.0;
    private const double TrainingXPValue = 0.05;
    private const double TrainingCapXPValue = 0.02;
    private const double PrisonerControlValue = 200.0;
    private const double WithdrawalControlValue = 400.0;
    private const double WardenFeedCost = 25.0;
    private const double MaxSlotCycles = 10.0;
    private const double LaborTicksPerHour = GameTime.TicksPerHour;

    private static MaterialInstall? LegInstall(RecipeFacts facts) => facts.MaterialInstallFor("Leg");

    private static bool IsMaterialPart(RecipeFacts facts, string hediff) => facts.MaterialInstallOfPart(hediff) is not null;

    private static bool HasMaterialInstall(RecipeFacts facts, string body) => facts.MaterialInstallFor(body) is not null;

    /// <summary>Reports whether recipe is the material install that serves the body part.</summary>
    private static bool MaterialInstallRecipe(RecipeFacts facts, string body, string recipe) =>
        facts.MaterialInstallFor(body) is { } m && m.Recipe == recipe;

    /// <summary>Reports whether the hediff is a material part serving a leg.</summary>
    private static bool LegPart(RecipeFacts facts, string hediff) =>
        facts.MaterialInstallOfPart(hediff) is { } m && m.Bodies.Contains("Leg");

    /// <summary>How many doctors should reach DoctorTrainingFloor: one, two from eight colonists.</summary>
    public static int DoctorsWanted(int colonists) => colonists >= 8 ? 2 : 1;

    /// <summary>Vanilla's XP from level to DoctorTrainingFloor, passion ignored.</summary>
    private static double MedicineXPToFloor(int level)
    {
        var xp = 0.0;
        for (var l = Math.Max(0, level); l < DoctorTrainingFloor; l++)
        {
            xp += 1000 * (l + 1);
        }
        return xp;
    }

    /// <summary>One install and removal cycle's XP for an install recipe of the given work.</summary>
    private static double CycleXP(double installWork) => SurgeryXPPerWork * (installWork + WoodRemovalWork);

    /// <summary>
    /// What one Medicine XP is worth and how many cycles a newly opened slot is expected to support: below the
    /// floor, until the best doctor under it reaches it; above it, while a restore waits on a doctor, MaxSlotCycles.
    /// </summary>
    public static (double PerXP, double Cycles) TrainingValue(PrisonerColony c, IReadOnlyList<SurgeryWant> wants, RecipeFacts facts)
    {
        if (LegInstall(facts) is not { } leg)
        {
            return (0, 0);
        }
        int at = 0, below = -1;
        foreach (var level in c.Medicine.Values)
        {
            if (level >= DoctorTrainingFloor)
            {
                at++;
            }
            else
            {
                below = Math.Max(below, level);
            }
        }
        if (at < DoctorsWanted(c.Colonists) && below >= 0)
        {
            return (TrainingXPValue, Math.Min(MaxSlotCycles, Math.Ceiling(MedicineXPToFloor(below) / CycleXP(leg.Work))));
        }
        foreach (var want in wants)
        {
            if (want.Reason == SurgeryReason.NoDoctor && c.Medicine.Count > 0)
            {
                return (TrainingCapXPValue, MaxSlotCycles);
            }
        }
        return (0, 0);
    }

    /// <summary>
    /// Picks at most one peg-leg step under the harvest's in-flight rule, ranked by BetterHarvest: a reinstall
    /// first, then control, then training; then the best gain less cost, then prisoner id. Unknown facts refuse.
    /// </summary>
    public static OrganHarvest? SelectPegCycle(
        Fact<EquatableArray<PrisonerFacts>> prisoners,
        Fact<PrisonerColony> colony,
        Fact<double> food,
        PrisonerPolicy p,
        IReadOnlyList<SurgeryWant> wants,
        RecipeFacts facts,
        IReadOnlySet<PawnId>? inFlight)
    {
        if (!prisoners.TryGetValue(out var rows) || !colony.TryGetValue(out var c) || SurgeryInFlight(rows, inFlight))
        {
            return null;
        }
        var (perXP, cycles) = TrainingValue(c, wants, facts);
        OrganHarvest? best = null;
        foreach (var row in rows)
        {
            foreach (var step in PegCycleSteps(row, c, food, p, perXP, cycles, facts))
            {
                if (best is null || BetterHarvest(step, best))
                {
                    best = step;
                }
            }
        }
        return best;
    }

    /// <summary>Reports whether a peg-leg step would be queued now; DetectRounds holds MaintainSurgery open on it.</summary>
    public static bool PegCycleWanted(RoundsFacts f, PrisonerPolicy p) =>
        SelectPegCycle(f.Prisoners, f.PrisonerColony, f.FoodDays, p, SelectSurgery(f.MedicalPawns, null, new SurgeryContext()).Wants, f.Recipes, null) is not null;

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

    /// <summary>The row's acceptable steps: a reinstall, a control removal and a training step, each only when it applies and pays off.</summary>
    private static List<OrganHarvest> PegCycleSteps(PrisonerFacts row, PrisonerColony c, Fact<double> food, PrisonerPolicy p, double perXP, double cycles, RecipeFacts facts)
    {
        var (intent, unknown) = PrisonerIntent(row, c, food, p);
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
        var trainable = eligible && !planned && intent != PrisonerInteractionMode.Release;
        row.Withdrawal.TryGetValue(out var withdrawal);
        var controlled = !planned && (trainable || withdrawal);
        var goodwillCost = SilverPerGoodwillPoint * Math.Max(0, -goodwill);
        var legsMissing = CountMissing(missing, "Leg");

        var removals = new List<SurgeryOperation>();
        var installs = new List<SurgeryOperation>();
        var cuts = new List<SurgeryOperation>();
        var pegLegs = 0;
        foreach (var op in ops)
        {
            op.PartDefName.TryGetValue(out var body);
            op.Recipe.TryGetValue(out var recipe);
            var ak = op.AddedPart.TryGetValue(out var added);
            var sk = op.IngredientsOnMap.TryGetValue(out var stocked);
            if (!HarvestAcceptable(op))
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
            else if ((op.Kind == SurgeryKind.Harvest || op.Kind == SurgeryKind.Amputate) && !ak && HasMaterialInstall(facts, body ?? "") && !(body == "Leg" && legsMissing > 0))
            {
                // Never remove the second leg.
                cuts.Add(op);
            }
        }

        void Step(PegCycleStep kind, SurgeryOperation op, double gain, double cost)
        {
            op.Recipe.TryGetValue(out var recipe);
            op.PartIndex.TryGetValue(out var part);
            op.Violation.TryGetValue(out var violation);
            var surgeon = kind == PegCycleStep.Training ? TrainingSurgeon(op, c) : default;
            output.Add(new OrganHarvest
            {
                Prisoner = row.Pawn,
                Recipe = recipe ?? "",
                Part = part,
                Gain = gain,
                Cost = cost,
                Violation = violation ?? "",
                Step = kind,
                Surgeon = surgeon,
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
                var unit = MedicineUnit(op, false);
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
        if (!trainable || perXP <= 0)
        {
            return output;
        }

        (double Gain, double Cost) CycleCost(double installWork, double unit)
        {
            var work = installWork + WoodRemovalWork;
            return (perXP * CycleXP(installWork), 3 * unit + LaborCost(work) + goodwillCost);
        }

        if (removals.Count > 0)
        {
            var op = removals[0];
            op.AddedPart.TryGetValue(out var added);
            var install = facts.MaterialInstallOfPart(added ?? "") ?? new MaterialInstall();
            var (gain, cost) = CycleCost(install.Work, MedicineUnit(op, false));
            if (gain > cost)
            {
                Step(PegCycleStep.Training, op, gain, cost);
            }
        }
        else if (installs.Count > 0)
        {
            SurgeryOperation? pick = null;
            var pickWork = -1.0;
            foreach (var op in installs)
            {
                op.Recipe.TryGetValue(out var recipe);
                var install = facts.MaterialInstallByRecipe(recipe ?? "") ?? new MaterialInstall();
                if (install.Work > pickWork)
                {
                    pick = op;
                    pickWork = install.Work;
                }
            }
            var (gain, cost) = CycleCost(pickWork, MedicineUnit(pick!, true));
            if (gain > cost)
            {
                Step(PegCycleStep.Training, pick!, gain, cost);
            }
        }
        else
        {
            // No slot open: amputate a natural part, priced once as a harvest and spread over the slot's cycles.
            foreach (var op in cuts)
            {
                op.PartDefName.TryGetValue(out var body);
                var harvest = HarvestCost(c, goodwill, false);
                if (harvest is not { } h || cycles <= 0)
                {
                    continue;
                }
                var install = facts.MaterialInstallFor(body ?? "") ?? new MaterialInstall();
                var (gain, cost) = CycleCost(install.Work, MedicineUnit(op, false));
                cost += (h + MedicineUnit(op, false) + LaborCost(WoodRemovalWork)) / cycles;
                if (gain > cost)
                {
                    Step(PegCycleStep.Training, op, gain, cost);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The doctor a training step's bill is restricted to: the lowest-Medicine doctor below DoctorTrainingFloor
    /// whose chance on the operation clears RestoreFailureCap, then pawn id. Empty when none does, which keeps
    /// vanilla's choice.
    /// </summary>
    private static PawnId TrainingSurgeon(SurgeryOperation op, PrisonerColony c)
    {
        PawnId pick = default;
        var pickLevel = DoctorTrainingFloor;
        foreach (var (doctor, chance) in op.DoctorChances)
        {
            if (!c.Medicine.TryGetValue(doctor, out var level) || level >= DoctorTrainingFloor || !double.IsFinite(chance) || 1 - chance > RestoreFailureCap + 1e-9)
            {
                continue;
            }
            if (level < pickLevel || (level == pickLevel && doctor.CompareTo(pick) < 0))
            {
                pick = doctor;
                pickLevel = level;
            }
        }
        return pick;
    }

    /// <summary>One medicine's value from an operation's medicine value: a wood install uses two, a removal one. Unknown reads as none.</summary>
    private static double MedicineUnit(SurgeryOperation op, bool install)
    {
        if (!op.MedicineValue.TryGetValue(out var v) || !double.IsFinite(v) || v < 0)
        {
            return 0;
        }
        return install ? v / 2 : v;
    }

    private static double LaborCost(double work) => SilverPerLaborHour * work / LaborTicksPerHour;
}
