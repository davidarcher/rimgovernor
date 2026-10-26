using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class ProductionBenchFixtures
{
    internal static RecipeAccess Recipe(string name, bool available = true, bool onBench = true) =>
        new RecipeAccess.OnBench(new RimGovernor.Brain.GameFacts.Observations.RecipeState { Recipe = name, AvailableNow = available, AvailableOnBench = onBench });

    internal static RimGovernor.Brain.GameFacts.Observations.BillStackRow Observed(string id, string definition, string token, bool usable, string? room = null) => new()
    {
        Bench = id, BenchDef = definition, Snapshot = new() { EntityId = id, Token = token },
        Usable = usable, Room = room is null ? null : new() { Id = room }, WorkSpeed = 1, Bills = [],

    };

    internal static ProductionBench Ordinary(string id, string definition, string token, bool usable, string? room = null) => new()
    {
        Observed = Observed(id, definition, token, usable, room), Use = new ProductionUse.Ordinary(),
    };

    internal static ProductionBench Cooking(string id, string definition, string token, bool usable, string? room = null) => new()
    {
        Observed = Observed(id, definition, token, usable, room),
        Use = new ProductionUse.Cooking(new() { Usable = usable, Recipes = [], Production = [] }),
    };

    internal static ProductionBench Butchering(string id, string definition, string token, bool usable, string? room = null) => new()
    {
        Observed = Observed(id, definition, token, usable, room),
        Use = new ProductionUse.Butchering(new() { Usable = usable, Recipes = [], HumanButchers = [], HumanCorpseMeat = [] }),
    };

    internal static ProductionBench InRoom(ProductionBench bench, string? room) => bench with
    {
        Observed = bench.Observed with { Room = room is null ? null : new() { Id = room } },
    };
}
