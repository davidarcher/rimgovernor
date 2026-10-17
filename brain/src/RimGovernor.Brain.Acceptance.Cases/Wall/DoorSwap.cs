using System.Globalization;
using System.Text.Json.Nodes;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/door-swap</c> (Go <c>door_swap.go</c>).</summary>
internal static class WallDoorSwap
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/door-swap",
        Scope = "Planned-ground door swap (#1245, epic #1249): a player door on a walled, roofed room's ring, " +
            "given DECONSTRUCT Designate.replace_with_wall, ends a Wall of the door's stuff on the same cell while the " +
            "room keeps its roof, built by force-ordered pawns under normal game rules. Reports which path vanilla " +
            "took (wall blueprint over the door in one apply, or deconstruct then place). A Go snapshot test cannot " +
            "see GenConstruct's placement rule, the ordered jobs or the built wall.",
        Start = new LabStart(),
        RequiredOps = [DebugGame.LabSpawnTool, FixtureStage.Tool],
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var (cx, cz) = CaseSupport.Center(s);

        // A 3x3 roofed room east of the centre with its door on the west side.
        var (walls, interior) = WallGeometry.Ring(cx + 3, cz - 2, cx + 7, cz + 2);
        var door = new WallCell(cx + 3, cz);
        var doorId = string.Empty;
        foreach (var c in walls)
        {
            var id = await WallGeometry.SpawnRingCellAsync(h, c, door, cancellationToken).ConfigureAwait(false);
            if (c == door)
            {
                doorId = id;
            }
        }

        await WallGeometry.RoofAsync(h, "roof", interior, cancellationToken).ConfigureAwait(false);
        var roofed = await WallGeometry.RoofedAsync(s, "roofed", interior, cancellationToken).ConfigureAwait(false);
        if (roofed != interior.Count)
        {
            throw new AcceptanceException($"roof the room: {roofed} roofed");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "swap-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var result = await WallGeometry.ApplyActionAsync(
            s,
            "swap",
            WallCases.DeconstructIntent("swap", JsonValue.Create(doorId), new JsonObject { ["replaceWithWall"] = true }),
            cancellationToken).ConfigureAwait(false);
        var applied = result["applied"].AsMap() ?? throw new AcceptanceException($"swap not applied: {result.ToJson()}");
        report["swap"] = applied;

        // A replacement named at apply is the in-place path: vanilla accepted the wall blueprint over the standing door.
        var inPlace = ContainsKey(applied, "replacementId");
        report["path"] = inPlace ? "blueprint_over_door" : "deconstruct_then_place";
        var where = string.Create(CultureInfo.InvariantCulture, $"{door.X},{door.Z}");
        for (var advanced = 0; advanced < 12000; advanced += 300)
        {
            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            var n = await WallGeometry.RoofedAsync(s, "roof-read-" + CaseSupport.Tag(advanced), interior, cancellationToken).ConfigureAwait(false);
            if (n != interior.Count)
            {
                throw new AcceptanceException($"the room lost roof during the swap at +{advanced + 300} ticks: {n} of {interior.Count} roofed");
            }

            var built = await h.BuildingsAsync(
                "cell-" + CaseSupport.Tag(advanced),
                new Observations.ListBuildingsRequest { Scope = TypedReads.Scope(s.Identity), DefNames = ["Wall", "Door"], Statuses = ["built"] },
                cancellationToken).ConfigureAwait(false);
            var here = built.Where(b => b.Building?.Position is { } p && p.X == door.X && p.Z == door.Z).ToList();
            var wall = here.Any(b => b.Building?.DefName == "Wall" && b.Stuff == "WoodLog");
            var doorStands = here.Any(b => b.Building?.DefName == "Door");

            if (wall && !doorStands)
            {
                report["swapped_after_ticks"] = advanced + 300;
                return;
            }
        }

        throw new AcceptanceException($"door at {where} not swapped for a WoodLog wall within 12000 ticks");
    }

    /// <summary>Finds <paramref name="key"/> anywhere in a decoded evidence tree.</summary>
    private static bool ContainsKey(JsonNode? value, string key)
    {
        switch (value)
        {
            case JsonObject map:
                foreach (var (name, child) in map)
                {
                    if (string.Equals(name, key, StringComparison.Ordinal) || ContainsKey(child, key))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                return array.Any(item => ContainsKey(item, key));
            default:
                return false;
        }
    }
}
