namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The pace a step's clock status reported: the running epoch's pacing reason, the rate it holds and its ceiling, and
/// native's effective speed (ticks per wall second, pauses included) (Go <c>StepPacing</c>).
/// </summary>
public sealed record StepPacing
{
    public string Reason { get; init; } = "";

    public uint PacedTps { get; init; }

    public uint Ceiling { get; init; }

    public double EffectiveTps { get; init; }

    public bool Player { get; init; }

    /// <summary>Reads the pace out of a status (Go <c>stepPacing</c>).</summary>
    public static StepPacing Of(PbClock.Status status)
    {
        var pacing = new StepPacing { EffectiveTps = status.EffectiveTicksPerSecond is not null ? status.EffectiveTicksPerSecond.GetValueOrDefault() : 0 };
        if (status.State is PbClock.StatusState.RunningCase { Value.Epoch: { } epoch })
        {
            pacing = pacing with
            {
                PacedTps = epoch.PacedTicksPerSecond.GetValueOrDefault(),
                Ceiling = epoch.MaxTicksPerSecond.GetValueOrDefault(),
                Player = epoch.Pacing is not null && epoch.Pacing.GetValueOrDefault() == PbClock.Pacing.PlayerAccelerated,
                Reason = epoch.PacingReason is not null ? PacingReasonName(epoch.PacingReason.GetValueOrDefault()) : "",
            };
        }
        return pacing;
    }

    /// <summary>The wire reason without its enum prefix, lower case: <c>frame_budget</c>, <c>ceiling</c>, ... (Go <c>PacingReasonName</c>).</summary>
    public static string PacingReasonName(PbClock.PacingReason reason)
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

    /// <summary>Adds the pace to a clock_step row: what the pacing reason and effective speed read (Go <c>publish</c>).</summary>
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
        if (Player)
        {
            extra["player_pacing"] = true;
        }
        if (EffectiveTps > 0)
        {
            extra["effective_tps"] = EffectiveTps;
        }
    }
}
