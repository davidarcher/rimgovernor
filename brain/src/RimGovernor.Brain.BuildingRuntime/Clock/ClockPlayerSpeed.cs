namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_player_speed.go.
internal static class ClockPlayerSpeed
{
    /// <summary>
    /// Starts at the speed the player last chose: Ultrafast when native reports none, and player acceleration
    /// exactly when that speed is Ultrafast (Go <c>followPlayerSpeed</c>).
    /// </summary>
    public static ClockStart Follow(ClockStart start, PbClock.Status status)
    {
        var speed = status.HasPlayerSpeed ? status.PlayerSpeed : PbClock.Speed.Ultrafast;
        return start with { Speed = speed, PlayerAccelerated = speed == PbClock.Speed.Ultrafast };
    }
}
