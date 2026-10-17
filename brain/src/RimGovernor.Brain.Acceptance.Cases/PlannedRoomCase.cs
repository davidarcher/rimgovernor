namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-room-one-builder</c> (#3313): one builder and one planned 3x3 wood room. Native rows, the
/// native routes between the room's sites and the native roof work are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedRoomCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/planned-room-one-builder",
        Scope = "One builder and an unplaced 3x3 wood room (wall run, door, plank floor, roof) on open ground with two wood stacks: the planned-site read "
            + "answers every site with a complete row for the builder, and every route probe between the room's sites (and from the builder to the roof) "
            + "with a known native route. The coupled forecast over those rows, routes and the native roof work schedules the whole room for that builder "
            + "within a day, site after site. Needs are held satisfied; does not prove live selection, placement or construction.",
        Start = new FixtureStart { Op = "test/planned_room_prepare", On = new LabStart { Colonists = 1 } },
        RequiredOps = ["test/planned_room_prepare", "test/planned_room_read"],
        QuietWorld = true,
        Crew = new Crew { Size = 1 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("planned-room", "test/planned_room_read", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Planned-room native read failed: " + read.ToJson());
            s.Report["planned_room"] = read.DeepClone();
        },
    };
}
