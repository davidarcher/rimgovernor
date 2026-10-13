using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>One colonist's carry inputs.</summary>
public sealed record MedicineCarryPawn
{
    public PawnId Id { get; init; }

    /// <summary>The Medicine stock count; unknown where the read did not carry the pawn's inventory stock, which is never written.</summary>
    public Fact<int> Current { get; init; }

    /// <summary>The pawn's MedicalCareCategory name; unknown carries nothing new.</summary>
    public Fact<string> Care { get; init; }

    /// <summary>Doctor work enabled.</summary>
    public bool Doctor { get; init; }

    /// <summary>A hunter or an armed ranged fighter.</summary>
    public bool Field { get; init; }
}

/// <summary>
/// Ports <c>medicine_carry.go</c>. The bot owns every colonist's Medicine inventory-stock count. Doctors carry 1-3
/// so they tend without walking to stock; field roles carry 1-2 so a doctor can tend in the field. Everyone else
/// carries none. The tier is the pawn's own medical care (native stocks the best medicine it allows); a care that
/// allows no medicine carries none. The total carried is capped so colony stock stays at or above the medical
/// reserve target: medicine already carried counts back into the budget, so the decision does not flip once pawns
/// pick it up.
/// </summary>
public static partial class PolicyFunctions
{
    public const int DoctorMaxCarry = DomainConstants.MaxMedicineCarry;
    public const int FieldMaxCarry = 2;

    /// <summary>A MedicalCareCategory that allows any medicine.</summary>
    private static bool CareAllowsMedicine(string care) => care is "HerbalOrWorse" or "NormalOrWorse" or "Best";

    /// <summary>
    /// Each pawn's wanted count. stock is the colony's usable medicine on the map, reserve the target it must stay
    /// at. Unknown stock or reserve plans nothing (Ok false).
    /// </summary>
    public static (Dictionary<PawnId, int> Want, bool Ok) MedicineCarryPlan(IReadOnlyList<MedicineCarryPawn> pawns, Fact<long> stock, Fact<long> reserve)
    {
        if (!stock.TryGetValue(out var s) || !reserve.TryGetValue(out var r))
        {
            return ([], false);
        }
        var budget = s - r;
        foreach (var p in pawns)
        {
            if (p.Current.TryGetValue(out var n) && n > 0)
            {
                budget += n;
            }
        }
        var want = new Dictionary<PawnId, int>();
        var doctors = new List<PawnId>();
        var field = new List<PawnId>();
        foreach (var p in pawns)
        {
            want[p.Id] = 0;
            if (!p.Care.TryGetValue(out var care) || !CareAllowsMedicine(care))
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
                        return (want, true);
                    }
                    want[id]++;
                    budget--;
                }
            }
        }
        return (want, true);
    }

    /// <summary>The settings to write: every pawn with a known count that differs from its planned one.</summary>
    public static List<PawnSettings> MedicineCarryChanges(IReadOnlyList<MedicineCarryPawn> pawns, Fact<long> stock, Fact<long> reserve)
    {
        var (want, ok) = MedicineCarryPlan(pawns, stock, reserve);
        var changes = new List<PawnSettings>();
        if (!ok)
        {
            return changes;
        }
        foreach (var p in pawns)
        {
            if (!p.Current.TryGetValue(out var current) || current == want.GetValueOrDefault(p.Id))
            {
                continue;
            }
            try
            {
                changes.Add(PawnSettings.NewMedicineCarrySetting(new DomainPawnId(p.Id.Value), want.GetValueOrDefault(p.Id)));
            }
            catch (DomainException)
            {
                // Go skipped a pawn whose setting failed validation.
            }
        }
        return changes;
    }

    /// <summary>The review's MedicineCarryOwed fact.</summary>
    public static Fact<bool> MedicineCarryOwed(Fact<IReadOnlyList<WorkPawn>> pawns, MedicalReserveReview reserve)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(MedicineCarryChanges(MedicineCarryRows(rows), reserve.Stock, reserve.Target).Count > 0);
    }

    /// <summary>Lifts the work census into carry rows.</summary>
    public static List<MedicineCarryPawn> MedicineCarryRows(IReadOnlyList<WorkPawn> pawns) => [.. pawns.Select(w => w.ToMedicineCarryPawn())];

    /// <summary>
    /// The pawn's carry inputs: the Medicine stock entry from the policy inputs, the medical care setting, Doctor
    /// or Hunting work enabled, and a violence-capable pawn with a ranged primary.
    /// </summary>
    public static MedicineCarryPawn ToMedicineCarryPawn(this WorkPawn w)
    {
        var p = new MedicineCarryPawn { Id = w.Id, Care = w.MedicalCare };
        if (w.PolicyInputs.TryGetValue(out var inputs))
        {
            var current = Known(0);
            foreach (var s in inputs.InventoryStock)
            {
                if (s.Group == "Medicine")
                {
                    current = Known(s.Count);
                }
            }
            p = p with { Current = current };
        }
        w.Work.TryGetValue(out var work);
        var doctor = false;
        var hunter = false;
        foreach (var x in work ?? [])
        {
            if (x.Disabled || x.Priority <= 0)
            {
                continue;
            }
            doctor = doctor || x.Work == WorkType.Doctor;
            hunter = hunter || x.Work == WorkType.Hunting;
        }
        var vk = w.ViolenceCapable.TryGetValue(out var violent);
        var rk = w.Ranged.TryGetValue(out var ranged);
        return p with { Doctor = doctor, Field = hunter || (vk && violent && rk && ranged) };
    }
}
