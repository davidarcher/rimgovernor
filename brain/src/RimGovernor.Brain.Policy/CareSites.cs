using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A care bed's state for one patient (task 4.1 of <c>redefine-habitat-requirements</c>): <see cref="Ready"/>, or the first
/// prerequisite it lacks. The bed's own (<see cref="SleepingRules.CareSlot"/>): IllegalBed, BedTaken, ConversionRequired,
/// NativeRefused. The patient's: PatientFleeing, PatientAggressive, PatientPrisonBreaking, PatientHostile
/// (<see cref="CareConduct"/>), Unreachable (no walk and no rescuer). The doctor's: NoReachingDoctor (no worker tends there; a
/// recovering patient, <see cref="SupportCarePatient.Tendable"/> false, waits on no tend).
/// </summary>
public enum CareBedState
{
    Ready,
    IllegalBed,
    BedTaken,
    ConversionRequired,
    NativeRefused,
    PatientFleeing,
    PatientAggressive,
    PatientPrisonBreaking,
    PatientHostile,
    Unreachable,
    NoReachingDoctor,
}

/// <summary>
/// One place a patient could be cared in: its demand and bed, its state, the ticks before the patient lies there (0 when it lies
/// there now, its walk, or its rescuer's walk and carry).
/// </summary>
public sealed record CareSite(string Demand, string Bed, CareBedState State, double MovementTicks);

public static class CareSites
{
    /// <summary>
    /// Every care patient's sites (<see cref="SupportCarePatient.Beds"/>), each in its state. A patient that does not let a
    /// doctor treat it blocks every site; a bed's own state comes next, then the movement, then whether any worker tends there.
    /// </summary>
    public static IReadOnlyList<CareSite> Of(SupportActivityRead read)
    {
        var sites = new List<CareSite>();
        foreach (var patient in read.Patients)
        {
            foreach (var bed in patient.Beds)
            {
                // Lying there now or no way there: no move to wait for (an unreachable bed is never ready).
                var ticks = bed.Movement is CareMovement.Walk walk ? walk.Ticks : bed.Movement is CareMovement.Carried carried ? carried.Ticks : 0d;
                var bedWorker = bed.Movement is CareMovement.AlreadyHere ? "" : bed.Id;
                var state = Conduct(patient.Conduct) ?? SleepingRules.CareSlot(bed, patient) switch
                {
                    CareBedState.Ready when bed.Movement is CareMovement.Unreachable => CareBedState.Unreachable,
                    CareBedState.Ready when patient.Tendable && !read.Workers.Any(w => w.Demand == patient.Demand && w.CareBed == bedWorker) => CareBedState.NoReachingDoctor,
                    var own => own,
                };
                sites.Add(new(patient.Demand, bed.Id, state, ticks));
            }
        }
        return sites;
    }

    /// <summary>The state a patient's conduct puts every site in, or null for a calm patient.</summary>
    internal static CareBedState? Conduct(CareConduct conduct) =>
        conduct == CareConduct.Fleeing ? CareBedState.PatientFleeing
        : conduct == CareConduct.Aggressive ? CareBedState.PatientAggressive
        : conduct == CareConduct.PrisonBreaking ? CareBedState.PatientPrisonBreaking
        : conduct == CareConduct.Hostile ? CareBedState.PatientHostile
        : null;
}
