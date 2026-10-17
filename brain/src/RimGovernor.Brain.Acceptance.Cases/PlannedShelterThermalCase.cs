namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-shelter-thermal</c> (#3323): the construction activity read's planned thermal read for an
/// unplaced planned shelter. Cell temperatures, roofs, the Heater def's heat and draw, and the power network a
/// connector at an empty cell would join are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedShelterThermalCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/planned-shelter-thermal",
        Scope = "An empty, unroofed planned 3x3 room (nothing placed) reads a ThermalCell for each of its 25 interior and ring cells at the native "
            + "temperature, unroofed and open; a planned Heater at its planned cell reads one incomplete ThermalSource with the def's heat per tick, default "
            + "target and power draw, joined to the wood generator's network through the conduit beside the room, with supply known exactly when that "
            + "network's surplus covers the draw; a planned bed reads no source. Does not prove Rounds composition or selection.",
        Start = new FixtureStart { Op = "test/planned_shelter_thermal_prepare", On = new LabStart() },
        RequiredOps = ["test/planned_shelter_thermal_prepare", "test/planned_shelter_thermal_read"],
        QuietWorld = true,
        Crew = new Crew { Size = 2 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("planned", "test/planned_shelter_thermal_read", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Planned shelter thermal read failed: " + read.ToJson());
            s.Report["planned_shelter_thermal"] = read.DeepClone();
        },
    };
}
