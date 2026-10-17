using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/cleared-floor</c> (Go <c>cleared_floor.go</c>).</summary>
internal static class WallClearedFloor
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/cleared-floor",
        Scope = "Planned-ground clearance end to end (#1245, epic #1249): a walled, roofed player room with a stool " +
            "on a laid WoodPlankFloor, all on planned ground, ends with no building, no roof and no constructed floor, " +
            "through the clearance order: the stool, then the walls and door (waiting on remove_roof), then " +
            "remove_floor once the cells are clear. A Go snapshot test cannot see vanilla's deconstruct and " +
            "RemoveFloor jobs, the roof guard or the terrain left behind.",
        Start = new LabStart(),
        RequiredOps = [DebugGame.LabSpawnTool, FixtureStage.Tool, "test/deconstruct_target"],
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var (cx, cz) = CaseSupport.Center(s);
        var (walls, interior) = WallGeometry.Ring(cx + 3, cz - 2, cx + 7, cz + 2);
        var door = new WallCell(cx + 3, cz);
        var stoolAt = new WallCell(cx + 5, cz);

        await WallGeometry.LayTerrainAsync(h, "lay-floor", interior, "WoodPlankFloor", cancellationToken).ConfigureAwait(false);
        var laid = await WallGeometry.FlooredAsync(s, "floor-laid", interior, cancellationToken).ConfigureAwait(false);
        if (laid != interior.Count)
        {
            throw new AcceptanceException($"lay the floor: {laid} floored");
        }

        var wallIds = new List<string>();
        foreach (var c in walls)
        {
            wallIds.Add(await WallGeometry.SpawnRingCellAsync(h, c, door, cancellationToken).ConfigureAwait(false));
        }

        var (stoolId, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Stool", Stuff = "WoodLog", X = stoolAt.X, Z = stoolAt.Z }, cancellationToken).ConfigureAwait(false);

        // Vanilla roofs the walls too (auto-roof), so clearance unroofs and reads the room's whole footprint; the fixture roofs its interior.
        List<WallCell> footprint = [.. walls, .. interior];
        await WallGeometry.RoofAsync(h, "roof", interior, cancellationToken).ConfigureAwait(false);
        var roofedInterior = await WallGeometry.RoofedAsync(s, "roofed", interior, cancellationToken).ConfigureAwait(false);
        if (roofedInterior != interior.Count)
        {
            throw new AcceptanceException($"roof the room: {roofedInterior} roofed");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "clear-authority", s.Identity, cancellationToken).ConfigureAwait(false);

        async Task Apply(string key, JsonObject intent)
        {
            intent["key"] = key;
            var result = await WallGeometry.ApplyActionAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
            if (result["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{key}: not applied: {result.ToJson()}");
            }
        }

        // Clearance order: furniture, then walls (roof first), then floors.
        await Apply("stool", WallCases.DeconstructIntent("stool", JsonValue.Create(stoolId), null)).ConfigureAwait(false);
        for (var i = 0; i < wallIds.Count; i++)
        {
            await Apply("wall-" + CaseSupport.Tag(i), WallCases.DeconstructIntent(string.Empty, JsonValue.Create(wallIds[i]), null)).ConfigureAwait(false);
        }

        await Apply("roof", new JsonObject { ["removeRoof"] = new JsonObject { ["cells"] = WallGeometry.CellArray(footprint) } }).ConfigureAwait(false);
        var floorsIssued = false;
        for (var advanced = 0; advanced < 15000; advanced += 300)
        {
            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            var tag = CaseSupport.Tag(advanced);
            var roof = await WallGeometry.RoofedAsync(s, "roof-read-" + tag, footprint, cancellationToken).ConfigureAwait(false);
            var standing = 0;
            var all = new List<string> { stoolId };
            all.AddRange(wallIds);
            for (var i = 0; i < all.Count; i++)
            {
                if (await WallGeometry.PresentAsync(h, "inspect-" + all[i], JsonValue.Create(all[i]), cancellationToken).ConfigureAwait(false))
                {
                    standing++;
                }
                else if (i > 0 && roof > 0)
                {
                    throw new AcceptanceException($"a wall came down at +{advanced + 300} ticks while {roof} room cells were still roofed");
                }
            }

            if (standing == 0 && roof == 0 && !floorsIssued)
            {
                report["buildings_cleared_after_ticks"] = advanced + 300;
                for (var i = 0; i < interior.Count; i++)
                {
                    await Apply(
                        "floor-" + CaseSupport.Tag(i),
                        new JsonObject
                        {
                            ["removeFloor"] = new JsonObject { ["cell"] = CaseSupport.Cell(interior[i].X, interior[i].Z), ["defName"] = "WoodPlankFloor" },
                        }).ConfigureAwait(false);
                }

                floorsIssued = true;
                continue;
            }

            if (floorsIssued && await WallGeometry.FlooredAsync(s, "floor-read-" + tag, interior, cancellationToken).ConfigureAwait(false) == 0)
            {
                report["floors_cleared_after_ticks"] = advanced + 300;
                return;
            }
        }

        throw new AcceptanceException($"room and floor not cleared within 15000 ticks (floors issued: {(floorsIssued ? "true" : "false")})");
    }
}
