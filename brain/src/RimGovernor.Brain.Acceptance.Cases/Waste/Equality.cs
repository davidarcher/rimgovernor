using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The waste/colony_facts_equality case is the permanent ColonyFacts equality check: on the waste fixture (item,
/// corpse and waste rows whose order matters, and ResourceSources) the native snapshot read with every read
/// optimization off and then on must be byte-identical (Go <c>equality.go</c>).
/// </summary>
internal static class WasteEquality
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "waste/colony_facts_equality",
        Scope = "ColonyFacts read optimizations change no byte: the snapshot with optimizations off and on " +
            "is identical, row order included, on a map with waste rows.",
        Start = new FixtureStart { Op = "test/waste_fixture", Args = new JsonObject { ["burial"] = false }, On = new LabStart() },
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // A roofed granite block with a face and a buried steel deposit gives the ResourceSources comparison mine,
        // buried and roof-support rows.
        _ = await s.Harness.CallAsync("buried-steel", "test/buried_steel", new JsonObject { ["action"] = "prepare" }, cancellationToken).ConfigureAwait(false);
        var result = await s.Harness.CallAsync("equality", "test/colony_facts_equality", [], cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in result)
        {
            s.Report[key] = value?.DeepClone();
        }

        if (result["equal"].AsBool() != true)
        {
            throw new AcceptanceException($"ColonyFacts differ with optimizations off and on: {result.ToJson()}");
        }

        if (result["wasteRows"].AsNumber() == 0 || result["upkeepItems"].AsNumber() == 0 || result["resourceRows"].AsNumber() == 0)
        {
            throw new AcceptanceException($"fixture read carries no waste, upkeep item or resource source rows to compare: {result.ToJson()}");
        }
    }
}
