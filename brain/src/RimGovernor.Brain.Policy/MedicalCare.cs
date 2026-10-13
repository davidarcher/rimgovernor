using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>A fresh native health assessment, independent of urgent tending.</summary>
public sealed record CarePawn
{
    /// <summary>Quest-protected guests may be tended, but never operated on during their visit.</summary>
    public bool QuestProtected { get; init; }

    public PawnId Id { get; init; }
    public Fact<string> Care { get; init; }
    public Fact<bool> LifeThreatening { get; init; }
    public Fact<EquatableArray<CareCondition>> Conditions { get; init; }
    public Fact<bool> Dead { get; init; }
    public Fact<bool> NeedsRest { get; init; }
    public Fact<bool> NeedsTend { get; init; }
    public Fact<bool> BadConditions { get; init; }

    /// <summary>Surgery facts ride on the same health read. Native computes every value through vanilla; nothing here is recomputed.</summary>
    public Fact<IReadOnlyList<MissingPart>> MissingParts { get; init; }

    public Fact<IReadOnlyList<SurgeryOperation>> Operations { get; init; }

    /// <summary>The medical bills queued on the patient: the surgery planner's in-flight evidence.</summary>
    public Fact<int> QueuedSurgeries { get; init; }

    /// <summary>Each queued bill's recipe; ReserveSurgeryStock holds their parts back from sale until the surgery runs.</summary>
    public IReadOnlyList<string> QueuedRecipes { get; init; } = [];

    /// <summary>The part items those bills install, by the catalog's recipe rows.</summary>
    public IReadOnlyList<Resource> QueuedItems { get; init; } = [];

    /// <summary>The added parts the pawn carries; unknown when the native read failed.</summary>
    public Fact<IReadOnlyList<InstalledPart>> InstalledParts { get; init; }
}

/// <summary>A missing or destroyed body part at its common missing ancestor; its descendants are implied.</summary>
public sealed record MissingPart
{
    public Fact<int> PartIndex { get; init; }
    public Fact<int> ParentIndex { get; init; }
    public Fact<string> PartDefName { get; init; }
    public Fact<string> ParentDefName { get; init; }
    public Fact<bool> Vital { get; init; }
}

/// <summary>Classifies an operation the way the surgery planner ranks it.</summary>
public enum SurgeryKind
{
    [WireName("")]
    Unknown = 0,
    Restore,
    Cure,
    Amputate,
    Install,
    Harvest,
    Other,
}

/// <summary>One available medical recipe on one target part. SuccessChance is vanilla's, for the best eligible doctor, bed and medicine; it is unknown when no doctor is eligible.</summary>
public sealed record SurgeryOperation
{
    public Fact<string> Recipe { get; init; }
    public Fact<string> PartDefName { get; init; }

    /// <summary>The part item the recipe installs by its catalog row, empty for a recipe that installs none.</summary>
    public Resource Item { get; init; }

    /// <summary>
    /// The capacity the recipe's part gives back relative to a natural part (1): the added part hediff's
    /// partEfficiency, 1 for a natural install, 0 for a recipe that installs no replacement part (an implant).
    /// </summary>
    public double Tier { get; init; }

    public Fact<int> PartIndex { get; init; }
    public SurgeryKind Kind { get; init; }
    public Fact<double> SuccessChance { get; init; }

    /// <summary>The same doctor's chance with an ideal bed and room; above SuccessChance when the bed holds it back.</summary>
    public Fact<double> DoctorSuccessChance { get; init; }

    public Fact<int> EligibleDoctors { get; init; }
    public Fact<bool> IngredientsOnMap { get; init; }
    public Fact<bool> Violation { get; init; }
    public Fact<bool> Lethal { get; init; }

    /// <summary>The market value in silver of what the removal spawns: a harvested organ or a removed added part.</summary>
    public Fact<double> YieldValue { get; init; }

    /// <summary>The added part hediff on a removal's target and the thing it spawns; unknown on a natural part.</summary>
    public Fact<string> AddedPart { get; init; }

    public Fact<string> YieldThing { get; init; }

    /// <summary>The market value of the medicine the operation uses.</summary>
    public Fact<double> MedicineValue { get; init; }

    /// <summary>Medicine the recipe takes is stocked but the patient's medical care forbids it; IngredientsOnMap is then false.</summary>
    public Fact<bool> CareLimited { get; init; }

    /// <summary>Each eligible doctor's SuccessChance by pawn; null when unread.</summary>
    public IReadOnlyDictionary<DomainPawnId, double>? DoctorChances { get; init; }
}

/// <summary>Native disease evidence without estimating missing values. Rates are instantaneous per game day, and may be negative during recovery.</summary>
public sealed record CareCondition
{
    public Fact<string> DefName { get; init; }
    public Fact<double> Severity { get; init; }
    public Fact<double> SeverityPerDay { get; init; }
    public Fact<double> Immunity { get; init; }
    public Fact<double> ImmunityPerDay { get; init; }
    public Fact<bool> Tended { get; init; }
    public Fact<double> TendQuality { get; init; }

    /// <summary>The condition's part in the race body's AllParts; unknown for a whole-body condition.</summary>
    public Fact<int> PartIndex { get; init; }
}

/// <summary>Retains unresolved patient identities across reviews and restart. Missing patients and deaths do not constitute observed recovery.</summary>
public sealed record MedicalCareHistory
{
    public IReadOnlyList<DiseaseRest> Resting { get; init; } = [];
    public bool CensusKnown { get; init; }
    public IReadOnlyList<PawnId> Patients { get; init; } = [];
    public IReadOnlyList<PawnId> Unknown { get; init; } = [];

    public void Validate()
    {
        var seen = new HashSet<PawnId>();
        foreach (var ids in new[] { Patients, Unknown })
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (!FoodId(id.Value) || seen.Contains(id) || (i > 0 && ids[i - 1] >= id))
                {
                    throw new DomainException("invalid medical care patient history");
                }
                seen.Add(id);
            }
        }
        ValidateDiseaseRest(Resting);
    }

    public Fact<bool> Recovered()
    {
        if (Patients.Count > 0 || Resting.Count > 0)
        {
            return Known(false);
        }
        if (!CensusKnown || Unknown.Count > 0)
        {
            return Unknown<bool>();
        }
        return Known(true);
    }
}

/// <summary>One added part (bionic, prosthetic, peg, archotech) read natively. Hediff is the added-part hediff def, Part the body part def and PartIndex its index; Item is the thing its removal spawns, which prices it; Tier is the hediff def's partEfficiency. No price is carried: the spent side prices Item.</summary>
public sealed record InstalledPart
{
    public string Hediff { get; init; } = "";
    public Fact<string> Part { get; init; }
    public Fact<int> PartIndex { get; init; }
    public Fact<Resource> Item { get; init; }
    public double Tier { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>MaintainMedicalReserves' Phase: care for the sick (rest and a hospital bed) before the medicine stock.</summary>
    public static readonly Phase MedicalCarePhase = new("care");

    public static readonly Phase MedicalReservesPhase = new("reserves");

    public static MedicalCareHistory ReviewMedicalCare(Fact<EquatableArray<CarePawn>> observed, MedicalCareHistory previous)
    {
        previous.Validate();
        var tracked = new HashSet<PawnId>();
        foreach (var ids in new[] { previous.Patients, previous.Unknown })
        {
            foreach (var id in ids)
            {
                tracked.Add(id);
            }
        }
        var known = observed.TryGetValue(out var rows);
        var resting = ReviewDiseaseRest(observed, previous.Resting);
        var patients = new List<PawnId>();
        var unknown = new List<PawnId>();
        var seen = new HashSet<PawnId>();
        foreach (var pawn in rows)
        {
            if (!FoodId(pawn.Id.Value) || seen.Contains(pawn.Id))
            {
                throw new DomainException("invalid medical care census");
            }
            seen.Add(pawn.Id);
            var dk = pawn.Dead.TryGetValue(out var dead);
            if (dk && dead)
            {
                continue;
            }
            tracked.Remove(pawn.Id);
            var rk = pawn.NeedsRest.TryGetValue(out var rest);
            var tk = pawn.NeedsTend.IsKnown;
            var bk = pawn.BadConditions.TryGetValue(out var bad);
            if (!dk || !rk || !tk || !bk)
            {
                unknown.Add(pawn.Id);
            }
            else if (rest || bad)
            {
                patients.Add(pawn.Id);
            }
        }
        unknown.AddRange(tracked);
        patients.Sort();
        unknown.Sort();
        var r = new MedicalCareHistory { CensusKnown = known, Resting = resting, Patients = patients, Unknown = unknown };
        r.Validate();
        return r;
    }

    /// <summary>Whether the medicine bill and herb harvest may act. They run in both phases: a sick colonist never pauses restocking. Only the hospital and medicine-tier steps are care-only.</summary>
    public static bool Restocks(this Phase p) => p.Value != "";
}
