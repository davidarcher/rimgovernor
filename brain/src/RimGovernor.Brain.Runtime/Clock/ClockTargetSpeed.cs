namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The speed the brain asks for while it owns the clock: a live option the run driver reads at each Run, so a
/// change takes effect from the next run (a run in progress keeps the speed it started at). Safe to set from any thread.
/// </summary>
public sealed class ClockTargetSpeed(RimGovernor.Brain.GameFacts.Clock.Speed initial)
{
    private int _speed = (int)Check(initial);

    /// <summary>The speed the next run starts at.</summary>
    public RimGovernor.Brain.GameFacts.Clock.Speed Speed => (RimGovernor.Brain.GameFacts.Clock.Speed)Volatile.Read(ref _speed);

    /// <summary>Sets the speed the next run starts at; undefined enum values are refused.</summary>
    public void Set(RimGovernor.Brain.GameFacts.Clock.Speed speed) => Volatile.Write(ref _speed, (int)Check(speed));

    private static RimGovernor.Brain.GameFacts.Clock.Speed Check(RimGovernor.Brain.GameFacts.Clock.Speed speed) =>
        Enum.IsDefined(speed)
            ? speed
            : throw new ArgumentOutOfRangeException(nameof(speed), speed, "the clock target speed is Normal, Fast, Superfast, Ultrafast or Hyperfast");
}
