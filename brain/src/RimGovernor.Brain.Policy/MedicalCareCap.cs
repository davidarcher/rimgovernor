using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One guest's care cap inputs.</summary>
public sealed record CarePatient
{
    public required O.PopulationPerson Person { get; init; }
    public required O.PawnState Observed { get; init; }

    public PawnId Id => Person.Pawn;

    public MedicalCare Care => Observed.Settings.Care is O.PawnCare.Configured care ? WorkRules.MedicalCareOf(care.MedicalCare) : throw new RequiredFactException("guest medical care settings");

    public EquatableArray<Hediff> Conditions => Observed.Health.Hediffs;

    public bool LifeThreatening => Observed.Health.LifeThreatening;
}

/// <summary>
/// Every pawn the colony cares for holds a standing MedicalCareCategory by what
/// it is worth, raised one tier while a serious condition lasts. The admin/work-assignments Need writes the caps through
/// PawnSettingsIntent. Colonists: NormalOrWorse while the usable industrial medicine stock meets
/// MedicalReservePolicy.TargetPerColonist per colonist, else HerbalOrWorse; never Best as a standing cap.
/// Prisoners being recruited, broken, converted or enslaved: NormalOrWorse; maintain-only, release, execution and
/// harvest targets: HerbalOrWorse. Guests: NormalOrWorse. Animals: HerbalOrWorse; bonded or combat animals
/// NormalOrWorse.
/// </summary>
public static partial class MedicalRules
{
    /// <summary>
    /// A condition worth one more medicine tier: a native life threat, plague, malaria past half severity, a wound
    /// infection not yet immune, or any immunizable disease losing its immunity race. An immunizable condition whose
    /// race cannot be projected (no immunity gain record, a value out of range) leaves the answer unknown unless a
    /// projected condition is already serious.
    /// </summary>
    public static (bool Serious, bool Known) SeriousCondition(IReadOnlyList<Hediff> conditions, bool life)
    {
        if (life)
        {
            return (true, true);
        }
        var incomplete = false;
        foreach (var row in conditions)
        {
            if (row.Immunizable is not { } immune)
            {
                continue;
            }
            var name = row.DefName;
            var severity = row.Severity;
            var immunity = immune.Immunity;
            if (!FiniteUnit(severity) || !FiniteUnit(immunity))
            {
                incomplete = true;
                continue;
            }
            if (immunity >= 1)
            {
                continue;
            }
            if (name == "Plague" || name == EmergencyRules.WoundInfection || (name == "Malaria" && severity >= 0.5))
            {
                return (true, true);
            }
            var (losing, known) = EmergencyRules.LosingImmunityRace(row);
            if (losing)
            {
                return (true, true);
            }
            incomplete = incomplete || !known;
        }
        return (false, !incomplete);
    }

    /// <summary>The cap with the serious raise applied; Ok is false when the raise is owed but the evidence is unknown.</summary>
    private static (MedicalCare Care, bool Ok) Capped(MedicalCare baseCare, bool raise, IReadOnlyList<Hediff> conditions, bool life)
    {
        if (!raise)
        {
            return (baseCare, true);
        }
        var (serious, known) = SeriousCondition(conditions, life);
        if (!known)
        {
            return (MedicalCare.None, false);
        }
        return serious ? (baseCare.Raised(), true) : (baseCare, true);
    }

    /// <summary>
    /// The colonists' standing cap: NormalOrWorse while stock of the normal-care medicine (potency rank 1) meets
    /// the per-colonist target, else HerbalOrWorse. The catalog must identify the normal-care medicine.
    /// </summary>
    public static MedicalCare ColonistCareBase(ItemFacts items, EquatableArray<Amount> resources, long colonists, MedicalReservePolicy p)
    {
        var normal = items.MedicineAt(1);
        long industrial = 0;
        foreach (var row in resources)
        {
            if (row.Resource == normal)
            {
                industrial += row.Count;
            }
        }
        if (colonists > 0 && industrial >= colonists * p.TargetPerColonist)
        {
            return MedicalCare.Normal;
        }
        return MedicalCare.Herbal;
    }

    /// <summary>A prisoner's standing cap and whether a serious condition may raise it.</summary>
    public static (MedicalCare Base, bool Raise) PrisonerCareBase(PrisonerFacts p)
    {
        if (p.Executing || PrisonerHarvestQueued(p))
        {
            return (MedicalCare.Herbal, false);
        }
        if (p.CurrentInteraction is PrisonerHeldInteraction.Settable { Mode: var mode }
            && mode is PrisonerInteractionMode.Recruit or PrisonerInteractionMode.ReduceResistance or PrisonerInteractionMode.Convert or PrisonerInteractionMode.Enslave)
        {
            return (MedicalCare.Normal, true);
        }
        return (MedicalCare.Herbal, true);
    }

    /// <summary>A queued bill whose recipe removes a clean natural part (a harvest) from the prisoner.</summary>
    public static bool PrisonerHarvestQueued(PrisonerFacts p)
    {
        foreach (var queued in p.QueuedRecipes)
        {
            foreach (var care in p.Operations)
            {
                if (care.Operation.Recipe == queued && care.Operation.Kind == SurgeryKind.Harvest)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>An animal's standing cap: NormalOrWorse when bonded or trained in Release, else HerbalOrWorse.</summary>
    public static MedicalCare AnimalCareBase(UpkeepAnimal a)
    {
        var combat = false;
        foreach (var t in a.Training)
        {
            combat = combat || (t.DefName == "Release" && t.Learned);
        }
        return a.Bonded || combat ? MedicalCare.Normal : MedicalCare.Herbal;
    }

    /// <summary>The care settings to write: every pawn whose care differs from its cap. A pawn with unknown cap inputs, an unknown animal care or an unknown census is left alone.</summary>
    public static List<PawnSettings> MedicalCareChanges(RoundsFacts f, MedicalReservePolicy p)
    {
        var output = new List<PawnSettings>();
        void Add(PawnId id, bool differs, MedicalCare want, bool ok)
        {
            if (!ok || !differs)
            {
                return;
            }
            try
            {
                output.Add(PawnSettings.NewMedicalCareSetting(id, want));
            }
            catch (DomainException)
            {
                // A setting that failed validation is dropped.
            }
        }

        var colonists = f.MedicalPawns;
        if (colonists.Count != 0)
        {
            var living = colonists.LongCount(c => !c.Dead);
            var baseCare = ColonistCareBase(f.Items, f.ObservedStock(), living, p);
            foreach (var c in colonists)
            {
                if (c.Dead) continue;
                var (want, capOk) = Capped(baseCare, true, c.Conditions, c.LifeThreatening);
                Add(c.Id, c.Care != want, want, capOk);
            }
        }
        foreach (var row in f.Population.Prisoners)
        {
            if (row.Dead)
            {
                continue;
            }
            var (baseCare, raise) = PrisonerCareBase(row);
            var (want, ok) = Capped(baseCare, raise, row.Conditions, row.LifeThreatening);
            Add(row.Pawn, row.MedicalCare != want, want, ok);
        }
        foreach (var g in f.Population.Guests)
        {
            var (want, ok) = Capped(MedicalCare.Normal, true, g.Conditions, g.LifeThreatening);
            Add(g.Id, g.Care != want, want, ok);
        }
        foreach (var a in f.AnimalUpkeep.Animals)
        {
            // An animal without care settings has no care to set.
            if (a.Care.Length == 0)
            {
                continue;
            }
            var (want, capOk) = Capped(AnimalCareBase(a), true, a.Conditions, a.LifeThreatening);
            Add(a.Id, a.Care != Wire.Name(want), want, capOk);
        }
        return output;
    }

    /// <summary>The review's MedicalCareOwed flag: a care change is owed.</summary>
    public static bool MedicalCareOwed(RoundsFacts f, MedicalReservePolicy p) => MedicalCareChanges(f, p).Count > 0;

    private static bool FiniteUnit(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1;
}
