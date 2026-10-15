namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The speed the brain asks for while it owns the clock: a live option the window driver reads at each admission, so a
/// change takes effect from the next window (a running window keeps the speed it started at). Safe to set from any thread.
/// </summary>
public sealed class ClockTargetSpeed(PbClock.Speed initial)
{
    private int _speed = (int)Check(initial);

    /// <summary>The speed the next window starts at.</summary>
    public PbClock.Speed Speed => (PbClock.Speed)Volatile.Read(ref _speed);

    /// <summary>Sets the speed the next window starts at; Unspecified is refused.</summary>
    public void Set(PbClock.Speed speed) => Volatile.Write(ref _speed, (int)Check(speed));

    private static PbClock.Speed Check(PbClock.Speed speed) =>
        Enum.IsDefined(speed) && speed != PbClock.Speed.Unspecified
            ? speed
            : throw new ArgumentOutOfRangeException(nameof(speed), speed, "the clock target speed is Normal, Fast, Superfast or Ultrafast");
}
