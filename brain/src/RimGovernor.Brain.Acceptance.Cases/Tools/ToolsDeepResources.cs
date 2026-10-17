using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>tools/deepresources</c> (Go <c>deepresources.go</c>): seeded deep resource lumps, scanners and drills read back exactly.</summary>
internal static class ToolsDeepResources
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "tools/deepresources",
        Tier = CaseTier.Off,
        Scope = "Seeded deep resource lumps aggregate by connected definition, both scanner types expose native state, and drills report exact deposit, depletion and controller ownership (#482, #538).",
        Start = new FixtureStart { Op = "test/deep_resources_seed", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static (double X, double Z) Cell(JsonNode? position) => (position.Member("x").AsNumber(), position.Member("z").AsNumber());

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var seed = s.Prepared;
        if (seed is null || seed["success"].AsBool() != true)
        {
            throw new AcceptanceException($"seed failed: {seed?.ToJson()}");
        }

        var ex = seed["x"].AsNumber();
        var ez = seed["z"].AsNumber();
        var facts = await ToolsFacts.ReadAsync(h, "deep-resources", cancellationToken).ConfigureAwait(false);
        var f = facts.Member("deepResources").Member("observed").AsMap()
            ?? throw new AcceptanceException($"deep resources unavailable: {facts.Member("deepResources").ToJson()}");

        var found = 0;
        foreach (var lump in f.Member("lumps").AsSlice())
        {
            var (x, z) = Cell(lump.Member("centre"));
            if (x < ex - 5 || x > ex + 5 || z < ez - 5 || z > ez + 5)
            {
                continue;
            }

            var def = lump.Member("defName").AsString();
            var count = lump.Member("count").AsNumber();
            var cells = lump.Member("cellCount").AsNumber();
            var valid = def == "Plasteel" && ((x == ex && z == ez && count == 300 && cells == 3) || (x == ex + 4 && z == ez && count == 25 && cells == 1));
            valid = valid || (def == "Steel" && x == ex + 3 && z == ez + 3 && count == 70 && cells == 2);
            if (!valid)
            {
                throw new AcceptanceException($"incorrect lump aggregation: {lump.ToJson()}");
            }

            found++;
        }

        if (found != 3)
        {
            throw new AcceptanceException($"got {found} fixture lumps, want 3");
        }

        foreach (var rows in new[] { f.Member("groundScanners").AsSlice(), f.Member("longRangeScanners").AsSlice() })
        {
            var foundScanner = false;
            foreach (var row in rows)
            {
                if (Cell(row.Member("position")).X != ex - 12)
                {
                    continue;
                }

                if (row.Member("built").AsBool() != true
                    || row.Member("powered").AsBool() != false
                    || row.Member("working").AsBool() != false
                    || row.Member("ticksToNextFind") is not { } ticks
                    || ticks.AsNumber() <= 0)
                {
                    throw new AcceptanceException($"incorrect scanner state: {row.ToJson()}");
                }

                foundScanner = true;
            }

            if (!foundScanner)
            {
                throw new AcceptanceException($"fixture scanner missing: {rows.Count} rows");
            }
        }

        // The drill on the lump reads its deposit; the drill over cleared ground reads depleted with no deposit.
        var drills = new Dictionary<(double, double), JsonNode?>();
        foreach (var row in f.Member("drills").AsSlice())
        {
            if (row.Member("defName").AsString() != "DeepDrill" || row.Member("depleted") is null || row.Member("designated").AsBool() != false)
            {
                throw new AcceptanceException($"incomplete drill row: {row.ToJson()}");
            }

            drills[Cell(row.Member("position"))] = row;
        }

        drills.TryGetValue((ex, ez), out var yielding);
        drills.TryGetValue((ex, ez - 8), out var depleted);
        if (yielding is null || yielding.Member("depleted").AsBool() != false || yielding.Member("resource").AsString() != "Plasteel" || yielding.Member("remaining").AsNumber() != 100)
        {
            throw new AcceptanceException($"incorrect yielding drill state: {yielding.ToJson()}");
        }

        if (depleted is null || depleted.Member("depleted").AsBool() != true || depleted.Member("resource") is not null || depleted.Member("remaining") is not null)
        {
            throw new AcceptanceException($"incorrect depleted drill state: {depleted.ToJson()}");
        }

        s.Report["deep_resources"] = f.DeepClone();
    }
}
