namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The closed sets the scheduler, admission and dispatch metrics draw their attribute values from, and the recorders. A value outside a set is reported as "other".</summary>
internal static class Metrics
{
    private static readonly Vocabulary StepCauseVocabulary = Vocabulary.Words(
        Wire.Name(StepCause.Timer), Wire.Name(StepCause.Wake), Wire.Name(StepCause.Settled), Wire.Name(StepCause.Full), Wire.Name(StepCause.Live));

    // Every policy Reason; a test keeps it equal to the constants declared in package policy.
    private static readonly Vocabulary RefusalReasonVocabulary = Vocabulary.Words(
        Wire.Name(Reason.NotReady), Wire.Name(Reason.AlreadyReserved), Wire.Name(Reason.UnknownFacts), Wire.Name(Reason.StaleFacts),
        Wire.Name(Reason.UnsafePlacement), Wire.Name(Reason.UnsafeThreat), Wire.Name(Reason.CriticalMedical), Wire.Name(Reason.MaterialRequired),
        Wire.Name(Reason.DependencyBlocked), Wire.Name(Reason.GeometryBlocked), Wire.Name(Reason.InvalidHeld),
        "unsafe_route", "roof_support_risk", "missing_storage", "urgent_competing_work");

    // The kinds the Worker may dispatch.
    private static readonly Vocabulary ActionKindVocabulary = Vocabulary.Where(kind =>
        Wire.TryParse<ActionKind>(kind, out var parsed) && Rounds.ExecutableKind(parsed));

    public static double MillisOf(TimeSpan d) => d.TotalMilliseconds;

    /// <summary>Counts a method admission by verdict and, per refusal, its reason.</summary>
    public static void RecordAdmission(bool admitted, IReadOnlyList<RimGovernor.Brain.Policy.Refusal> refused)
    {
        if (admitted)
        {
            Tel.Admission("admitted", Tel.NoReason);
            return;
        }
        if (refused.Count == 0)
        {
            Tel.Admission("refused", RefusalReasonVocabulary.Label("unspecified"));
            return;
        }
        foreach (var f in refused)
        {
            Tel.Admission("refused", RefusalReasonVocabulary.Label(Wire.Name(f.Reason)));
        }
    }

    /// <summary>Counts one dispatched action: failed on a returned error, refused (once per reason) when the game or executor declined it, else applied.</summary>
    public static void RecordDispatchOutcome(ActionKind kind, ExecutorResult result, Exception? error)
    {
        var label = ActionKindVocabulary.Label(Wire.Name(kind));
        if (error is not null)
        {
            Tel.ActionOutcome(label, "failed", Tel.NoReason);
        }
        else if (result.Refused.Count > 0)
        {
            foreach (var r in result.Refused)
            {
                Tel.ActionOutcome(label, "refused", RefusalReasonVocabulary.Label(Wire.Name(r.Reason)));
            }
        }
        else
        {
            Tel.ActionOutcome(label, "applied", Tel.NoReason);
        }
    }

    /// <summary>Records what a finished scheduler step measured. The figures come from the fields the step's clock_step row carries, so the metric and the row cannot disagree about when one applies.</summary>
    public static void RecordStepMetrics(StepCause cause, TimeSpan elapsed, TimeSpan criticalWave, IReadOnlyDictionary<string, object?> row)
    {
        Tel.SchedulerStep(StepCauseVocabulary.Label(Wire.Name(cause)), MillisOf(elapsed));
        if (criticalWave > TimeSpan.Zero)
        {
            Tel.CriticalWave(MillisOf(criticalWave));
        }
        if (row.GetValueOrDefault("stop_latency_ms") is double stopLatency)
        {
            Tel.StopLatency(stopLatency);
        }
        if (row.GetValueOrDefault("controller_pause_ms") is double pause)
        {
            Tel.ControllerPause(pause);
        }
        if (row.GetValueOrDefault("stop_pause_s") is double stopPause)
        {
            Tel.StopPause(stopPause * 1000);
        }
        if (row.GetValueOrDefault("effective_tps") is double tps)
        {
            Tel.TicksPerSecond(tps);
        }
    }
}
