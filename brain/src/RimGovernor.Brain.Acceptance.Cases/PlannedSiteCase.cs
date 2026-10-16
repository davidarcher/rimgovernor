namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-site</c> (#3273): the construction activity read's planned sites. Native work to build,
/// construction speed, pathfinding and blueprint legality are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedSiteCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/planned-site",
        Scope = "Unplaced planned walls, a door and a shelf on open ground read finite native approach, delivery and finish ticks per pawn, "
            + "with finish equal to WorkToBuild(stuff)/ConstructionSpeed; a wall over a standing wall and one on deep water read canPlace=false with native reasons, "
            + "the standing colony wall unestimable and deep water illegal (#3274). Walls over grass and a grown tree read a Cut op (harvestWork / native work per tick), "
            + "over a steel stack a Haul op with finite pickup and carry to a stockpile, and over natural rock a Mine op (ceil(HP/80) hits at round(100/MiningSpeed)). "
            + "Does not prove Rounds composition or scheduling.",
        Start = new FixtureStart { Op = "test/planned_site_prepare", On = new LabStart() },
        RequiredOps = ["test/planned_site_prepare", "test/planned_site_read", "test/planned_site_clearing"],
        QuietWorld = true,
        Crew = new Crew { Size = 3 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("planned", "test/planned_site_read", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Planned-site native read failed: " + read.ToJson());
            s.Report["planned_sites"] = read.DeepClone();
            var clearing = await s.Harness.CallAsync("planned", "test/planned_site_clearing", [], ct).ConfigureAwait(false);
            if (clearing["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Planned-site clearing read failed: " + clearing.ToJson());
            s.Report["planned_clearing"] = clearing.DeepClone();
        },
    };
}
