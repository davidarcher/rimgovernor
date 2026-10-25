
namespace RimGovernor.Brain.Policy;

/// <summary>One colonist's carry inputs.</summary>
public sealed record MedicineCarryPawn
{
    public PawnId Id { get; init; }

    /// <summary>The observed Medicine inventory-stock count.</summary>
    public int Current { get; init; }

    /// <summary>The pawn's configured medical care tier.</summary>
    public MedicalCare Care { get; init; }

    /// <summary>Doctor work enabled.</summary>
    public bool Doctor { get; init; }

    /// <summary>A hunter or an armed ranged fighter.</summary>
    public bool Field { get; init; }
}

/// <summary>
/// The bot owns every colonist's Medicine inventory-stock count. Doctors carry 1-3
/// so they tend without walking to stock; field roles carry 1-2 so a doctor can tend in the field. Everyone else
/// carries none. The tier is the pawn's own medical care (native stocks the best medicine it allows); a care that
/// allows no medicine carries none. The total carried is capped so colony stock stays at or above the medical
/// reserve target: medicine already carried counts back into the budget, so the decision does not flip once pawns
/// pick it up.
/// </summary>
public static partial class MedicineCarryRules
{
    public const int DoctorMaxCarry = PawnSettingsConstants.MaxMedicineCarry;
    public const int FieldMaxCarry = 2;

    /// <summary>A MedicalCareCategory that allows any medicine.</summary>
    private static bool CareAllowsMedicine(MedicalCare care) => care is MedicalCare.Herbal or MedicalCare.Normal or MedicalCare.Best;

    /// <summary>
    /// Each pawn's wanted count. stock is the colony's usable medicine on the map, reserve the target it must stay
    /// at. Both counts come from the current complete medicine census.
    /// </summary>
    public static Dictionary<PawnId, int> MedicineCarryPlan(IReadOnlyList<MedicineCarryPawn> pawns, long stock, long reserve)
    {

        var budget = stock - reserve;
        foreach (var p in pawns)
        {
            if (p.Current > 0)
            {
                budget += p.Current;
            }
        }
        var want = new Dictionary<PawnId, int>();
        var doctors = new List<PawnId>();
        var field = new List<PawnId>();
        foreach (var p in pawns)
        {
            want[p.Id] = 0;
            if (!CareAllowsMedicine(p.Care))
            {
                continue;
            }
            if (p.Doctor)
            {
                doctors.Add(p.Id);
            }
            else if (p.Field)
            {
                field.Add(p.Id);
            }
        }
        doctors.Sort();
        field.Sort();
        // One unit a round: every doctor, then every field pawn, so the first units spread across roles before
        // anyone tops up.
        for (var round = 1; round <= DoctorMaxCarry; round++)
        {
            foreach (var (ids, max) in new[] { (doctors, DoctorMaxCarry), (field, FieldMaxCarry) })
            {
                if (round > max)
                {
                    continue;
                }
                foreach (var id in ids)
                {
                    if (budget <= 0)
                    {
                        return want;
                    }
                    want[id]++;
                    budget--;
                }
            }
        }
        return want;
    }

    /// <summary>The settings to write: every pawn whose observed count differs from its planned one.</summary>
    public static List<PawnSettings> MedicineCarryChanges(IReadOnlyList<MedicineCarryPawn> pawns, long stock, long reserve)
    {
        var want = MedicineCarryPlan(pawns, stock, reserve);
        var changes = new List<PawnSettings>();

        foreach (var p in pawns)
        {
            if (p.Current == want.GetValueOrDefault(p.Id))
            {
                continue;
            }
            try
            {
                changes.Add(PawnSettings.NewMedicineCarrySetting(p.Id, want.GetValueOrDefault(p.Id)));
            }
            catch (DomainException)
            {
                // A pawn whose setting failed validation is skipped.
            }
        }
        return changes;
    }

    /// <summary>The review's MedicineCarryOwed fact.</summary>
    public static bool MedicineCarryOwed(WorkPawnsRead pawns, MedicalReserveReview reserve)
    {
        var rows = pawns.Pawns;
        return MedicineCarryChanges(MedicineCarryRows(rows), reserve.Stock, reserve.Target).Count > 0;
    }

    /// <summary>Lifts the work census into carry rows.</summary>
    public static List<MedicineCarryPawn> MedicineCarryRows(IReadOnlyList<WorkPawn> pawns) => [.. pawns.Select(w => w.ToMedicineCarryPawn())];

    /// <summary>
    /// The pawn's carry inputs: the Medicine stock entry from the policy inputs, the medical care setting, Doctor
    /// or Hunting work enabled, and a violence-capable pawn with a ranged primary.
    /// </summary>
    public static MedicineCarryPawn ToMedicineCarryPawn(this WorkPawn w)
    {
        var p = new MedicineCarryPawn { Id = w.Id, Care = w.Care is PawnCare.Configured care ? WorkRules.MedicalCareOf(care.MedicalCare) : throw new DomainException($"colonist {w.Id} has no care setting") };
        var current = 0;
        foreach (var s in w.PolicyInputs.InventoryStock)
        {
            if (s.Group == "Medicine")
            {
                current = s.Count;
            }
        }
        p = p with { Current = current };
        var doctor = false;
        var hunter = false;
        foreach (var x in w.Work.Priorities())
        {
            if (x.Disabled || x.Priority <= 0)
            {
                continue;
            }
            doctor = doctor || x.Work == WorkType.Doctor;
            hunter = hunter || x.Work == WorkType.Hunting;
        }
        return p with { Doctor = doctor, Field = hunter || (w.ViolenceCapable && w.Ranged) };
    }
}
