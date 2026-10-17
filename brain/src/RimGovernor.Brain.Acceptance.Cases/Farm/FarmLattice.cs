using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/lattice-sow</c> (Go <c>lattice.go</c>): issue #2290. A growing zone of trees (blockAdjacentSow) sown by
/// colonists ends with exactly ceil(N/2)*ceil(M/2) trees, every one on the 2x2 lattice anchored on the zone's minimum
/// corner, while a rice zone of the same size is filled completely. A Go snapshot cannot cover it: the rule is a Harmony
/// patch on vanilla WorkGiver_GrowerSow.JobOnCell and the sow-blocking is vanilla's own (PlantUtility.AdjacentSowBlocker).
/// </summary>
internal static class FarmLattice
{
    private const string Prepare = "test/lattice_sow_prepare";
    private const int Width = 5;
    private const int Height = 4;

    // LatticeTicks bounds the native sowing run: two small zones sown by the fixture's colonists take well under three game days.
    private const ulong LatticeTicks = 3UL * Wait.TicksPerDay;

    internal static AcceptanceCase Case() => new()
    {
        Name = "farm/lattice-sow",
        Scope = "Issue #2290: a growing zone of trees (blockAdjacentSow) sown by colonists ends with exactly ceil(N/2)*ceil(M/2) trees, "
            + "every one on the 2x2 lattice anchored on the zone's minimum corner, while a rice zone of the same size is filled completely. "
            + "A Go snapshot cannot cover it: the rule is a Harmony patch on vanilla WorkGiver_GrowerSow.JobOnCell and the sow-blocking is vanilla's own (PlantUtility.AdjacentSowBlocker).",
        Start = new FixtureStart { Op = Prepare, On = new LabStart(), Args = new JsonObject { ["width"] = Width, ["height"] = Height } },
        NoKeep = true,
        RequiredOps = [Prepare],
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed record Zone(string Plant, int Cells, int MinX, int MinZ, IReadOnlyList<(int X, int Z)> Planted);

    // Each growing zone by load id, from the zones read joined to the cells read.
    private static async Task<Dictionary<string, Zone>> ReadZonesAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var zones = new Dictionary<string, Zone>(StringComparer.Ordinal);
        foreach (var zone in await s.Harness.GrowingZonesAsync(label, s.Identity, cancellationToken).ConfigureAwait(false))
        {
            var minX = zone.Cells.Count == 0 ? 0 : zone.Cells.Min(c => c.X);
            var minZ = zone.Cells.Count == 0 ? 0 : zone.Cells.Min(c => c.Z);
            zones[zone.Id] = new Zone(zone.Crop, zone.Cells.Count, minX, minZ, [.. zone.Planted.Select(c => (c.X, c.Z))]);
        }

        return zones;
    }

    private static JsonObject Evidence(IReadOnlyDictionary<string, Zone> zones)
    {
        var map = new JsonObject();
        foreach (var (id, zone) in zones)
        {
            map[id] = new JsonObject
            {
                ["plant"] = zone.Plant,
                ["cells"] = zone.Cells,
                ["minX"] = zone.MinX,
                ["minZ"] = zone.MinZ,
                ["planted"] = new JsonArray([.. zone.Planted.Select(c => (JsonNode?)new JsonArray(c.X, c.Z))]),
            };
        }

        return map;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var h = s.Harness;
        var oakId = prepared["oakZone"].AsString();
        var riceId = prepared["riceZone"].AsString();
        if (oakId.Length == 0 || oakId == riceId)
        {
            throw new AcceptanceException($"fixture: unexpected lattice staging: {prepared.ToJson()}");
        }

        report["fixture"] = prepared;
        var pitch = DomainConstants.TreeLatticePitch;
        var wantTrees = (Width + pitch - 1) / pitch * ((Height + pitch - 1) / pitch);
        var wantRice = Width * Height;

        Dictionary<string, Zone> zones = [];
        Exception? waitError = null;
        try
        {
            _ = await RunAt.RunUntilAsync(
                h,
                "lattice-sow",
                LatticeTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    zones = await ReadZonesAsync(s, "lattice-census", ct).ConfigureAwait(false);
                    var oakNow = zones.GetValueOrDefault(oakId);
                    var riceNow = zones.GetValueOrDefault(riceId);
                    var oakCount = oakNow?.Planted.Count ?? 0;
                    var riceCount = riceNow?.Planted.Count ?? 0;
                    return (string.Create(CultureInfo.InvariantCulture, $"oak={oakCount} rice={riceCount}"), oakCount >= wantTrees && riceCount >= wantRice);
                },
                quiet: true,
                report,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            waitError = e;
        }

        report["lattice_zones"] = Evidence(zones);
        if (waitError is not null)
        {
            throw new AcceptanceException($"sowing: {waitError.Message}", waitError);
        }

        var oak = zones.GetValueOrDefault(oakId) ?? new Zone("", 0, 0, 0, []);
        var rice = zones.GetValueOrDefault(riceId) ?? new Zone("", 0, 0, 0, []);
        if (oak.Planted.Count != wantTrees)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"oak zone holds {oak.Planted.Count} trees, want ceil({Width}/2)*ceil({Height}/2)={wantTrees}"));
        }

        foreach (var (x, z) in oak.Planted)
        {
            if ((x - oak.MinX) % pitch != 0 || (z - oak.MinZ) % pitch != 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"oak at ({x},{z}) is off the lattice anchored at ({oak.MinX},{oak.MinZ})"));
            }
        }

        if (rice.Planted.Count != wantRice)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rice zone holds {rice.Planted.Count} plants, want every one of {wantRice} cells"));
        }
    }
}
