using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/cleared-room</c> (Go <c>cleared_room.go</c>).</summary>
internal static class WallClearedRoom
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/cleared-room",
        Scope = "Cleared-ground deconstruction (#1366, epic #1249): a small walled, roofed player room inside the " +
            "cleared ground ends unroofed with its walls and door gone, through a remove_roof intent and " +
            "DECONSTRUCT Designate whose walls wait (designated, waitingForRoof) until pawns remove the roof; " +
            "a wall of a room straddling the ground's edge is admitted and held by the roof-support wait. A Go snapshot test cannot see vanilla room " +
            "geometry, the NoRoof area work or the native job guard.",
        Start = new LabStart(),
        RequiredOps = [DebugGame.LabSpawnTool, CombatFixtures.StageTool, "test/deconstruct_target"],
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

        // Room A (inside the ground) east of the centre, room B (straddling) west of it; each a 3x3 interior with a door on its centre-facing side.
        var (aWalls, aInterior) = WallGeometry.Ring(cx + 3, cz - 2, cx + 7, cz + 2);
        var (bWalls, bInterior) = WallGeometry.Ring(cx - 7, cz - 2, cx - 3, cz + 2);
        var aDoor = new WallCell(cx + 3, cz);
        var bDoor = new WallCell(cx - 3, cz);

        async Task<List<string>> Spawn(List<WallCell> walls, WallCell door)
        {
            var ids = new List<string>();
            foreach (var c in walls)
            {
                ids.Add(await WallGeometry.SpawnRingCellAsync(h, c, door, cancellationToken).ConfigureAwait(false));
            }

            return ids;
        }

        var aIds = await Spawn(aWalls, aDoor).ConfigureAwait(false);
        var bIds = await Spawn(bWalls, bDoor).ConfigureAwait(false);
        await WallGeometry.RoofAsync(h, "roof-a", aInterior, cancellationToken).ConfigureAwait(false);
        var roofA = await WallGeometry.RoofedAsync(s, "roofed-a", aInterior, cancellationToken).ConfigureAwait(false);
        if (roofA != aInterior.Count)
        {
            throw new AcceptanceException($"roof room A: {roofA} roofed");
        }

        await WallGeometry.RoofAsync(h, "roof-b", bInterior, cancellationToken).ConfigureAwait(false);
        var roofB = await WallGeometry.RoofedAsync(s, "roofed-b", bInterior, cancellationToken).ConfigureAwait(false);
        if (roofB != bInterior.Count)
        {
            throw new AcceptanceException($"roof room B: {roofB} roofed");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "clear-authority", s.Identity, cancellationToken).ConfigureAwait(false);

        // The straddling room's west wall (x0, cz) is admitted (no room-extent refusal); the work giver holds it while its removal would drop a roof.
        var straddle = await WallGeometry.ApplyActionAsync(s, "straddle", WallCases.DeconstructIntent("straddle", JsonValue.Create(bIds[2]), null), cancellationToken).ConfigureAwait(false);
        if (straddle["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"straddling room wall not admitted: {straddle.ToJson()}");
        }

        report["straddle"] = straddle;

        // Room A: designate its walls first, which wait on the roof, then the roof.
        for (var i = 0; i < aIds.Count; i++)
        {
            var key = "wall-a-" + CaseSupport.Tag(i);
            var result = await WallGeometry.ApplyActionAsync(s, key, WallCases.DeconstructIntent(key, JsonValue.Create(aIds[i]), null), cancellationToken).ConfigureAwait(false);
            var applied = result["applied"].AsMap() ?? throw new AcceptanceException($"{key}: not applied: {result.ToJson()}");
            if (i == 0)
            {
                if (!applied.ToJson().Contains("\"waitingForRoof\":true", StringComparison.Ordinal))
                {
                    throw new AcceptanceException($"{key}: roofed room wall does not report waiting for the roof: {applied.ToJson()}");
                }

                report["first_wall"] = applied;
            }
        }

        // Vanilla roofs the walls too (auto-roof), so clearance unroofs the room's whole footprint.
        List<WallCell> aFootprint = [.. aWalls, .. aInterior];
        var roof = await WallGeometry.ApplyActionAsync(
            s,
            "roof-a",
            new JsonObject { ["key"] = "roof-a", ["removeRoof"] = new JsonObject { ["cells"] = WallGeometry.CellArray(aFootprint) } },
            cancellationToken).ConfigureAwait(false);
        if (roof["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"remove_roof not applied: {roof.ToJson()}");
        }

        report["remove_roof"] = roof;
        var unroofedAt = -1;
        for (var advanced = 0; advanced < 12000; advanced += 300)
        {
            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            var n = await WallGeometry.RoofedAsync(s, "roof-read-" + CaseSupport.Tag(advanced), aFootprint, cancellationToken).ConfigureAwait(false);
            if (n == 0 && unroofedAt < 0)
            {
                unroofedAt = advanced + 300;
            }

            var standing = 0;
            foreach (var id in aIds)
            {
                if (await WallGeometry.PresentAsync(h, "inspect-" + id, JsonValue.Create(id), cancellationToken).ConfigureAwait(false))
                {
                    standing++;
                }
            }

            // The walls hold the roof: none may come down while it stands.
            if (n > 0 && standing < aIds.Count)
            {
                throw new AcceptanceException($"a wall came down at +{advanced + 300} ticks while {n} room cells were still roofed");
            }

            if (n == 0 && standing == 0)
            {
                report["unroofed_after_ticks"] = unroofedAt;
                report["cleared_after_ticks"] = advanced + 300;
                var kept = await WallGeometry.RoofedAsync(s, "roof-b-kept", bInterior, cancellationToken).ConfigureAwait(false);
                if (kept != bInterior.Count)
                {
                    throw new AcceptanceException($"straddling room lost its roof: {kept} roofed");
                }

                return;
            }
        }

        throw new AcceptanceException($"room A not cleared within 12000 ticks (unroofed at {unroofedAt})");
    }
}
