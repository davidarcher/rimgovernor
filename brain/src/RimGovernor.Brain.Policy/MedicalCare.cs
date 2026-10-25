
namespace RimGovernor.Brain.Policy;

/// <summary>A fresh native health assessment, independent of urgent tending. A dead colonist has no operations.</summary>
public sealed record CarePawn
{
    /// <summary>Quest-protected guests may be tended, but never operated on during their visit.</summary>
    public bool QuestProtected { get; init; }

    public required PawnId Id { get; init; }

    /// <summary>The colonist's standing medical care.</summary>
    public required MedicalCare Care { get; init; }

    public required bool LifeThreatening { get; init; }

    /// <summary>Every hediff on the pawn, none filtered.</summary>
    public required EquatableArray<Hediff> Conditions { get; init; }

    public required bool Dead { get; init; }

    public required bool NeedsRest { get; init; }

    /// <summary>Some hediff is one the catalog classifies as bad.</summary>
    public required bool BadConditions { get; init; }

    public required EquatableArray<CareOperation> Operations { get; init; }

    /// <summary>The medical bills queued on the patient: the surgery planner's in-flight evidence.</summary>
    public required int QueuedSurgeries { get; init; }

    /// <summary>The part items those bills install, by the catalog's recipe rows.</summary>
    public IReadOnlyList<Resource> QueuedItems { get; init; } = [];
}

/// <summary>
/// One native operation with what the catalog says of its recipe: the part item it installs (empty for none) and
/// the capacity that part gives back relative to a natural part (1): the added part hediff's partEfficiency, 1 for a
/// natural install, 0 for a recipe that installs no replacement part (an implant).
/// </summary>
public sealed record CareOperation
{
    public required SurgeryOperation Operation { get; init; }

    public required Resource Item { get; init; }

    public required double Tier { get; init; }
}

/// <summary>Retains unresolved patient identities across reviews and restart. Missing patients and deaths do not constitute observed recovery.</summary>
public sealed record MedicalCareHistory
{
    public IReadOnlyList<DiseaseRest> Resting { get; init; } = [];
    public IReadOnlyList<PawnId> Patients { get; init; } = [];
    public IReadOnlyList<PawnId> Unresolved { get; init; } = [];

    public void Validate()
    {
        var seen = new HashSet<PawnId>();
        foreach (var ids in new[] { Patients, Unresolved })
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (!FoodForecastRules.FoodId(id.Value) || seen.Contains(id) || (i > 0 && ids[i - 1] >= id))
                {
                    throw new DomainException("invalid medical care patient history");
                }
                seen.Add(id);
            }
        }
        DiseaseRestRules.ValidateDiseaseRest(Resting);
    }

    public bool Recovered() => Patients.Count == 0 && Resting.Count == 0 && Unresolved.Count == 0;
}

public static partial class MedicalRules
{

    public static MedicalCareHistory ReviewMedicalCare(EquatableArray<CarePawn> observed, MedicalCareHistory previous)
    {
        previous.Validate();
        var tracked = new HashSet<PawnId>();
        foreach (var ids in new[] { previous.Patients, previous.Unresolved })
        {
            foreach (var id in ids)
            {
                tracked.Add(id);
            }
        }
        var rows = observed;
        var resting = DiseaseRestRules.ReviewDiseaseRest(observed, previous.Resting);
        var patients = new List<PawnId>();
        var unresolved = new List<PawnId>();
        var seen = new HashSet<PawnId>();
        foreach (var pawn in rows)
        {
            if (!FoodForecastRules.FoodId(pawn.Id.Value) || seen.Contains(pawn.Id))
            {
                throw new DomainException("invalid medical care census");
            }
            seen.Add(pawn.Id);
            if (pawn.Dead)
            {
                continue;
            }
            tracked.Remove(pawn.Id);
            if (pawn.NeedsRest || pawn.BadConditions)
            {
                patients.Add(pawn.Id);
            }
        }
        unresolved.AddRange(tracked);
        patients.Sort();
        unresolved.Sort();
        var r = new MedicalCareHistory { Resting = resting, Patients = patients, Unresolved = unresolved };
        r.Validate();
        return r;
    }
}
