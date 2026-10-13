namespace RimGovernor.Brain.Policy;

// Go: capture.go

/// <summary>
/// One candidate capture target. Completion is Prisoner true (the native Capture job carries the pawn into a
/// prisoner bed and marks it a prisoner); a downed pawn who has simply stopped moving is not evidence of a
/// completed capture. Capturer eligibility is identical to Rescue's RescuerFacts (an undrafted, capable
/// performer), so it is reused rather than duplicated.
/// </summary>
public sealed record CapturePatientFacts
{
    public PawnId Pawn { get; init; }

    public string SnapshotToken { get; init; } = "";

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Prisoner { get; init; }

    public Fact<string> ExistingJobDef { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Pairs the warden (WardenFor over the roster, empty when none qualifies) when it is an available
    /// undrafted capturer, otherwise the first such capturer by ID, with the first downed, not-yet-captured
    /// patient (by ID for a stable, deterministic choice), mirroring SelectRescue. This is a proposal only;
    /// EvaluateCapture re-validates the chosen pair.
    /// </summary>
    public static (PawnId Capturer, PawnId Patient)? SelectCapture(
        IEnumerable<RescuerFacts> capturers,
        IEnumerable<CapturePatientFacts> patients,
        PawnId warden
    )
    {
        static bool EligibleCapturer(RescuerFacts r)
        {
            var dk = r.Dead.TryGetValue(out var dead);
            var wk = r.Downed.TryGetValue(out var downed);
            var tk = r.Drafted.TryGetValue(out var drafted);
            var mk = r.MentalState.TryGetValue(out var mental);
            var fk = r.PlayerForced.TryGetValue(out var forced);
            var qk = r.QueuedJobs.TryGetValue(out var queued);
            var ek = r.ExistingJobDef.TryGetValue(out var existing);
            if (!dk || !wk || !tk || !mk || !fk || !qk || !ek)
            {
                return false;
            }

            return !dead && !downed && !drafted && !mental && !forced && queued == 0 && existing != "Capture";
        }

        static bool EligiblePatient(CapturePatientFacts p)
        {
            var dk = p.Dead.TryGetValue(out var dead);
            var wk = p.Downed.TryGetValue(out var downed);
            var pk = p.Prisoner.TryGetValue(out var prisoner);
            var ek = p.ExistingJobDef.TryGetValue(out var existing);
            if (!dk || !wk || !pk || !ek)
            {
                return false;
            }

            return !dead && downed && !prisoner && existing != "Capture";
        }

        var capturerPool = capturers.Where(EligibleCapturer).ToList();
        var patientPool = patients.Where(EligiblePatient).ToList();
        if (capturerPool.Count == 0 || patientPool.Count == 0)
        {
            return null;
        }

        var capturer = capturerPool.OrderByDescending(r => r.Pawn == warden).ThenBy(r => r.Pawn.Value, StringComparer.Ordinal).First();
        var patient = patientPool.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal).First();
        return (capturer.Pawn, patient.Pawn);
    }
}
