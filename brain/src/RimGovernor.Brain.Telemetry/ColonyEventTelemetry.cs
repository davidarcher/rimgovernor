namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The kinds and shared field keys of the rows native emits, one per colony event
/// (otel-signals.md#clock-and-colony-events). Native is the one source of these rows; the brain does not republish them.
/// </summary>
public static class ColonyEventTelemetry
{
    public const string HoldKind = "clock_hold";
    public const string ResumeKind = "clock_resume";
    public const string AuthorityKind = "clock_authority";
    public const string AlertKind = "clock_alert";
    public const string NotificationKind = "clock_notification";
    public const string InjuryKind = "clock_injury";
    public const string HostilesClearedKind = "clock_hostiles_cleared";
    public const string InvalidationKind = "clock_invalidation";
    public const string LongEventKind = "clock_long_event";
    public const string PauseFailedKind = "clock_pause_failed";

    /// <summary>The row component.</summary>
    public const string Component = "clock";

    /// <summary>The event's game tick (its observation context's tick).</summary>
    public const string TickKey = "tick";

    /// <summary>Native's Unix ms when the event was observed; on a resume row, the resume time.</summary>
    public const string ObservedAtKey = "observed_at_unix_ms";

    /// <summary>A hold's <c>HoldReason</c> name (<c>LetterPause</c>, <c>Review</c>, ...); a resume's is <see cref="FromKey"/>.</summary>
    public const string ReasonKey = "reason";

    /// <summary>The tick a hold was first raised at.</summary>
    public const string DetectedTickKey = "detected_tick";

    /// <summary>The tick a hold's hazard arose, when its evidence carries one.</summary>
    public const string OccurrenceTickKey = "occurrence_tick";

    /// <summary>Native's Unix ms when the hold took (the status's <c>HeldAtUnixMs</c>).</summary>
    public const string StopAtMsKey = "stop_at_ms";

    /// <summary>The hold reason a resume ended.</summary>
    public const string FromKey = "from";
}
