using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>One guest's care cap inputs.</summary>
public sealed record CarePatient
{
    public PawnId Id { get; init; }
    public Fact<string> Care { get; init; }
    public Fact<EquatableArray<CareCondition>> Conditions { get; init; }
    public Fact<bool> LifeThreatening { get; init; }
}

/// <summary>
/// Ports <c>medical_care_cap.go</c>. Every pawn the colony cares for holds a standing MedicalCareCategory by what
/// it is worth, raised one tier while a serious condition lasts. EnsureWorkAssignments writes the caps through
/// PawnSettingsIntent. Colonists: NormalOrWorse while the usable industrial medicine stock meets
/// MedicalReservePolicy.TargetPerColonist per colonist, else HerbalOrWorse; never Best as a standing cap.
/// Prisoners being recruited, broken, converted or enslaved: NormalOrWorse; maintain-only, release, execution and
/// harvest targets: HerbalOrWorse. Guests: NormalOrWorse. Animals: HerbalOrWorse; bonded or combat animals
/// NormalOrWorse.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// A condition worth one more medicine tier: a native life threat, plague, malaria past half severity, a wound
    /// infection not yet immune, or any immunizable disease losing its immunity race. Unknown evidence is unknown
    /// unless a known condition is already serious.
    /// </summary>
    public static (bool Serious, bool Known) SeriousCondition(Fact<EquatableArray<CareCondition>> conditions, Fact<bool> life)
    {
        if (Positive(life))
        {
            return (true, true);
        }
        var ck = conditions.TryGetValue(out var rows);
        var lk = life.IsKnown;
        var incomplete = !ck || !lk;
        foreach (var row in rows)
        {
            var nk = row.DefName.TryGetValue(out var name);
            var ik = row.Immunity.TryGetValue(out var immunity);
            if (!ik)
            {
                if (!nk)
                {
                    incomplete = true;
                }
                continue;
            }
            var sk = row.Severity.TryGetValue(out var severity);
            if (!nk || !sk || !FiniteUnit(severity) || !FiniteUnit(immunity))
            {
                incomplete = true;
                continue;
            }
            if (immunity >= 1)
            {
                continue;
            }
            if (name == "Plague" || name == WoundInfection || (name == "Malaria" && severity >= 0.5))
            {
                return (true, true);
            }
            var (losing, known) = LosingImmunityRace(row);
            if (losing)
            {
                return (true, true);
            }
            incomplete = incomplete || !known;
        }
        return (false, !incomplete);
    }

    /// <summary>The cap with the serious raise applied; Ok is false when the raise is owed but the evidence is unknown.</summary>
    private static (MedicalCare Care, bool Ok) Capped(MedicalCare baseCare, bool raise, Fact<EquatableArray<CareCondition>> conditions, Fact<bool> life)
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
    /// the per-colonist target, else HerbalOrWorse. Unknown without the catalog's medicines.
    /// </summary>
    public static (MedicalCare Care, bool Ok) ColonistCareBase(ItemFacts items, Fact<EquatableArray<Amount>> resources, long colonists, MedicalReservePolicy p)
    {
        var known = resources.TryGetValue(out var rows);
        Resource normal;
        try
        {
            normal = items.MedicineAt(1);
        }
        catch (DomainException)
        {
            return (MedicalCare.None, false);
        }
        if (!known)
        {
            return (MedicalCare.None, false);
        }
        long industrial = 0;
        foreach (var row in rows!)
        {
            if (row.Resource == normal)
            {
                industrial += row.Count;
            }
        }
        if (colonists > 0 && industrial >= colonists * p.TargetPerColonist)
        {
            return (MedicalCare.Normal, true);
        }
        return (MedicalCare.Herbal, true);
    }

    /// <summary>A prisoner's standing cap and whether a serious condition may raise it.</summary>
    public static (MedicalCare Base, bool Raise) PrisonerCareBase(PrisonerFacts p)
    {
        if (p.Executing || PrisonerHarvestQueued(p))
        {
            return (MedicalCare.Herbal, false);
        }
        if (p.CurrentInteraction.TryGetValue(out var mode)
            && mode is PrisonerInteractionMode.Recruit or PrisonerInteractionMode.ReduceResistance or PrisonerInteractionMode.Convert or PrisonerInteractionMode.Enslave)
        {
            return (MedicalCare.Normal, true);
        }
        return (MedicalCare.Herbal, true);
    }

    /// <summary>A queued bill whose recipe removes a clean natural part (a harvest) from the prisoner.</summary>
    public static bool PrisonerHarvestQueued(PrisonerFacts p)
    {
        p.Operations.TryGetValue(out var ops);
        foreach (var queued in p.QueuedRecipes)
        {
            foreach (var op in ops ?? [])
            {
                op.Recipe.TryGetValue(out var recipe);
                if (recipe == queued && op.Kind == SurgeryKind.Harvest)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>An animal's standing cap: NormalOrWorse when bonded or trained in Release, else HerbalOrWorse; unknown without the bond read.</summary>
    public static (MedicalCare Care, bool Ok) AnimalCareBase(UpkeepAnimal a)
    {
        if (!a.Bonded.TryGetValue(out var bonded))
        {
            return (MedicalCare.None, false);
        }
        var combat = false;
        foreach (var t in a.Training)
        {
            t.Learned.TryGetValue(out var learned);
            combat = combat || (t.Def == "Release" && learned);
        }
        return bonded || combat ? (MedicalCare.Normal, true) : (MedicalCare.Herbal, true);
    }

    /// <summary>The care settings to write: every pawn whose known care differs from its cap. A pawn with unknown care, unknown cap inputs or an unknown census is left alone.</summary>
    public static List<PawnSettings> MedicalCareChanges(RoundsFacts f, MedicalReservePolicy p)
    {
        var output = new List<PawnSettings>();
        void Add(PawnId id, Fact<string> current, MedicalCare want, bool ok)
        {
            if (!ok || !current.TryGetValue(out var care) || care == Wire.Name(want))
            {
                return;
            }
            try
            {
                output.Add(PawnSettings.NewMedicalCareSetting(new DomainPawnId(id.Value), want));
            }
            catch (DomainException)
            {
                // Go dropped a setting that failed validation.
            }
        }

        if (f.MedicalPawns.TryGetValue(out var colonists))
        {
            long living = 0;
            foreach (var c in colonists)
            {
                if (c.Dead.TryGetValue(out var dead) && !dead)
                {
                    living++;
                }
            }
            var (baseCare, ok) = ColonistCareBase(f.Items, f.Resources, living, p);
            if (ok)
            {
                foreach (var c in colonists)
                {
                    if (!c.Dead.TryGetValue(out var dead) || dead)
                    {
                        continue;
                    }
                    var (want, capOk) = Capped(baseCare, true, c.Conditions, c.LifeThreatening);
                    Add(c.Id, c.Care, want, capOk);
                }
            }
        }
        f.Prisoners.TryGetValue(out var prisoners);
        foreach (var row in prisoners)
        {
            if (!row.Dead.TryGetValue(out var dead) || dead)
            {
                continue;
            }
            var (baseCare, raise) = PrisonerCareBase(row);
            var (want, ok) = Capped(baseCare, raise, row.Conditions, row.LifeThreatening);
            Add(new PawnId(row.Pawn.Value), row.MedicalCare, want, ok);
        }
        f.Guests.TryGetValue(out var guests);
        foreach (var g in guests)
        {
            var (want, ok) = Capped(MedicalCare.Normal, true, g.Conditions, g.LifeThreatening);
            Add(g.Id, g.Care, want, ok);
        }
        f.AnimalUpkeep.Animals.TryGetValue(out var animals);
        foreach (var a in animals)
        {
            var (baseCare, ok) = AnimalCareBase(a);
            if (!ok)
            {
                continue;
            }
            var (want, capOk) = Capped(baseCare, true, a.Conditions, a.LifeThreatening);
            Add(a.ID, a.Care, want, capOk);
        }
        return output;
    }

    /// <summary>The review's MedicalCareOwed fact: a care change is owed. Unknown pawns owe nothing.</summary>
    public static Fact<bool> MedicalCareOwed(RoundsFacts f, MedicalReservePolicy p) => Known(MedicalCareChanges(f, p).Count > 0);

    private static bool FiniteUnit(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1;
}
