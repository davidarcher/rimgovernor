using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>waste/colony-facts-equality</c>: the permanent ColonyFacts equality check. On the waste fixture (item, corpse and
/// waste rows whose order matters) and a buried steel deposit (ResourceSources mine, buried and roof-support rows), the
/// native snapshot read with every read optimization off and then on must be byte-identical. The comparison runs inside
/// test/colony_facts_equality because it toggles native read switches between two reads on one game-thread frame.
/// </summary>
internal static class WasteEquality
{
    private const string WasteTool = "test/waste_fixture";
    private const string BuriedSteelTool = "test/buried_steel";
    private const string EqualityTool = "test/colony_facts_equality";

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Waste = ServedOps.Inject(WasteTool);

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> BuriedSteel = new(BuriedSteelTool, _ => ServedOps.Args(("action", "prepare")), ServedOps.Done(BuriedSteelTool));

    private static readonly FixtureOp<NoArgs, Fx.FactsEquality> Compare = ServedOps.Read<Fx.FactsEquality>(EqualityTool);

    internal static TypedCase Case() => new()
    {
        Name = "waste/colony-facts-equality",
        Scope = "ColonyFacts read optimizations change no byte: the native snapshot with optimizations off and on is identical, "
            + "row order included, on a map with waste, upkeep item and resource source rows. Only the native read can be compared.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [WasteTool, BuriedSteelTool, EqualityTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("waste", Waste, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("buried-steel", BuriedSteel, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var result = await s.FixtureAsync("equality", Compare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("equality", ServedOps.Json(result));
        if (result.WasteRows == 0 || result.UpkeepItems == 0 || result.ResourceRows == 0)
        {
            throw new AcceptanceException($"the fixture read carries no waste, upkeep item or resource source rows to compare: {result}");
        }

        if (!result.Equal)
        {
            throw new AcceptanceException($"ColonyFacts differ with optimizations off and on: {result}");
        }
    }
}
