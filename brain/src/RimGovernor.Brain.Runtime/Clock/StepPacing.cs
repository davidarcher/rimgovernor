namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The pace a step's clock status reported: the running epoch's pacing reason, the rate it holds and its ceiling, and
/// native's effective speed (ticks per wall second, pauses included).
/// </summary>
public sealed record StepPacing
{
    public string Reason { get; init; } = "";

    public uint PacedTps { get; init; }

    public uint Ceiling { get; init; }

    public double EffectiveTps { get; init; }

    /// <summary>Reads the pace out of a status.</summary>
    public static StepPacing Of(RimGovernor.Brain.GameFacts.Clock.Status status)
    {
        var pacing = new StepPacing { EffectiveTps = status.EffectiveTicksPerSecond };
        if (status.Pacing is RimGovernor.Brain.GameFacts.Clock.Pacing.RunningCase running)
        {
            pacing = pacing with
            {
                PacedTps = running.TicksPerSecond,
                Ceiling = running.CeilingTicksPerSecond,
                Reason = PacingReasonName(running.Reason),
            };
        }
        return pacing;
    }

    /// <summary>The wire reason without its enum prefix, lower case: <c>frame_budget</c>, <c>heartbeat</c>, ....</summary>
    public static string PacingReasonName(RimGovernor.Brain.GameFacts.Clock.PacingReason reason)
    {
        var name = reason.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    /// <summary>Adds the pace to a clock_step row: what the pacing reason and effective speed read.</summary>
    public void Publish(IDictionary<string, object?> extra)
    {
        if (Reason.Length != 0)
        {
            extra["pacing_reason"] = Reason;
            extra["paced_tps"] = PacedTps;
        }
        if (Ceiling != 0)
        {
            extra["tps_ceiling"] = Ceiling;
        }
        if (EffectiveTps > 0)
        {
            extra["effective_tps"] = EffectiveTps;
        }
    }
}
