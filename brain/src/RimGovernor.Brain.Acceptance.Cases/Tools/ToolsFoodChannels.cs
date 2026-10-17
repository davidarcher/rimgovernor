using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>tools/foodchannels</c> (Go <c>foodchannels.go</c>): the core-only food source census on the lab.</summary>
internal static class ToolsFoodChannels
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "tools/foodchannels",
        Tier = CaseTier.Off,
        Scope = "Core-only food source census on the lab: spawned berry-bush forage and absent Odyssey water; a fixture cow reports native milk fullness (#421).",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task<JsonObject> ReadAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var facts = await ToolsFacts.ReadAsync(h, label, cancellationToken).ConfigureAwait(false);
        return facts.Member("foodChannels").Member("observed").AsMap()
            ?? throw new AcceptanceException($"food channels unavailable: {facts.Member("foodChannels").ToJson()}");
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var c = LabWorld.LabMapSize / 2;
        for (var i = 0; i < 3; i++)
        {
            _ = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Plant_Berry", X = c - 6 + i, Z = c - 6, Unowned = true }, cancellationToken).ConfigureAwait(false);
        }

        var baseline = await ReadAsync(h, "foodchannels-baseline", cancellationToken).ConfigureAwait(false);
        var forage = baseline.Member("forage").AsSlice();
        if (baseline.Member("fishableWater") is not null || forage.Count == 0)
        {
            throw new AcceptanceException($"core baseline must have forage and no Odyssey fishing: {baseline.ToJson()}");
        }

        s.Report["baseline_forage"] = forage.Count;
        _ = await h.CallAsync("foodchannels-cow-fixture", "test/husbandry_setup", [], cancellationToken).ConfigureAwait(false);
        var herd = await ReadAsync(h, "foodchannels-herd", cancellationToken).ConfigureAwait(false);
        var gatherable = herd.Member("gatherable").AsSlice();
        foreach (var row in gatherable)
        {
            if (row.Member("race").AsString() == "Cow" && row.Member("resource").AsString() == "Milk" && row.Member("fullness") is { } raw)
            {
                var fullness = raw.AsNumber();
                if (fullness >= 0 && fullness <= 1)
                {
                    s.Report["cow_fullness"] = fullness;
                    return;
                }
            }
        }

        throw new AcceptanceException($"fixture cow lacks observed milk fullness: {herd.Member("gatherable").ToJson()}");
    }
}
