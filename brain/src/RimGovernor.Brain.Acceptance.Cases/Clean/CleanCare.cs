namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clean/care</c> (Go <c>care.go</c>): opportunistic cleaning around a patient's bed (#2521). With eight old filth
/// beside a bed, the patched native WorkGiver_DoBill hands a doctor a Clean job capped at five filth before a surgery
/// bill, and a finished tend hands the doctor the same job; a doctor with Cleaning at priority 0 gets the surgery bill
/// and no follow-up job.
/// </summary>
internal static class CleanCare
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "clean/care",
        Scope = "Opportunistic cleaning around a patient's bed (#2521): with eight old filth beside a bed, the patched native "
            + "WorkGiver_DoBill hands a doctor a Clean job capped at five filth before a surgery bill, and a finished tend hands "
            + "the doctor the same job; a doctor with Cleaning at priority 0 gets the surgery bill and no follow-up job. "
            + "A Go snapshot cannot prove the Harmony patches on vanilla job selection and the tend driver's finalizer.",
        Start = new LabStart(),
        RequiredOps = ["test/care_cleaning"],
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "care-cleaning-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var row = await h.CallAsync("care-cleaning", "test/care_cleaning", [], cancellationToken).ConfigureAwait(false);
        s.Report["probe"] = row.DeepClone();
        if (row["success"].AsBool() != true)
        {
            throw new AcceptanceException($"care_cleaning refused: {row.ToJson()}");
        }

        if (row["supervisorActive"].AsBool() != true)
        {
            throw new AcceptanceException($"supervisor not active: {row.ToJson()}");
        }

        if (row["surgery"].AsString() != "Clean" || row["surgeryTargets"].AsNumber() != 5)
        {
            throw new AcceptanceException($"a doctor must clean the five-filth cap before the surgery bill: {row.ToJson()}");
        }

        if (row["tend"].AsString() != "Clean" || row["tendTargets"].AsNumber() != 5)
        {
            throw new AcceptanceException($"a finished tend must hand the doctor the five-filth clean job: {row.ToJson()}");
        }

        if (row["surgeryDisabled"].AsString() != "DoBill" || row["surgeryDisabledTargets"].AsNumber() != 0)
        {
            throw new AcceptanceException($"the Cleaning-disabled doctor must go straight to the surgery bill: {row.ToJson()}");
        }

        if (row["tendDisabled"].AsString() != "none" || row["tendDisabledTargets"].AsNumber() != 0)
        {
            throw new AcceptanceException($"the Cleaning-disabled doctor must not clean after tending: {row.ToJson()}");
        }
    }
}
