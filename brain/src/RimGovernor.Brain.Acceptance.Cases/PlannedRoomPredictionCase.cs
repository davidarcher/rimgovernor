namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-room-prediction</c> (#3339): the construction activity read's room prediction for a planned
/// room before its ring is built. The game's room role workers and impressiveness stage over a room that does not yet
/// exist are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedRoomPredictionCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/planned-room-prediction",
        Scope = "A wooden bed in an open 3x3 patch: the room prediction read, asked for that patch closed by a wood wall ring with a door, predicts "
            + "the role, impressiveness (within 1e-3), stage, cell and open-roof counts the game's own room reports once the fixture spawns that ring, "
            + "and lists the bed. Does not prove Rounds composition, the bed thought or selection.",
        Start = new FixtureStart { Op = "test/planned_room_prediction_prepare", On = new LabStart() },
        RequiredOps = ["test/planned_room_prediction_prepare", "test/planned_room_prediction_read"],
        QuietWorld = true,
        Crew = new Crew { Size = 1 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("planned", "test/planned_room_prediction_read", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Planned room prediction read failed: " + read.ToJson());
            s.Report["planned_room_prediction"] = read.DeepClone();
        },
    };
}
