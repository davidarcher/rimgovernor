using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bills/census</c>: the typed bench census behind observations_read_bills and observations_read_recipes. A staged
/// campfire appears once with its CAS token, the token agrees with the colony-facts cooking row and the bench's recipe
/// catalog, the catalog carries each recipe's availability, and a blank or unknown bench id is refused.
/// </summary>
internal static class BillsCensus
{
    private const string Bench = "campfire";

    internal static TypedCase Case() => new()
    {
        Name = "bills/census",
        Scope = "observations_read_bills and observations_read_recipes read the native bench: a staged campfire appears once "
            + "in the bill census with a CAS token for its entity, the token agrees with the colony-facts cooking row and the "
            + "recipe catalog snapshot, every recipe row carries its availability, and a blank bench id (INVALID_REQUEST) and "
            + "an unknown one (NOT_FOUND) are refused. Read-only.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 3,
                Height = 3,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 15,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty],
            },
            Buildings = [new Fx.StageBuilding { Name = Bench, Def = "Campfire", Cells = [new(1, 1)] }],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var id = s.Staged!.Buildings[Bench].Ids.Single();
        var problems = new List<string>();
        var census = await s.CallAsync("bills", NativeOps.ReadBills, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var rows = census.Benches.Where(b => b.Bench == id).ToList();
        if (rows is not [{ Snapshot: { Token.Length: > 0 } snapshot } row] || snapshot.EntityId != id)
        {
            throw new AcceptanceException($"bill census: want the staged bench {id} once with a CAS token, got {rows.Count} rows: {string.Join("; ", rows)}");
        }

        var duplicated = census.Benches.GroupBy(b => b.Bench).Where(g => g.Key.Length == 0 || g.Count() > 1).Select(g => g.Key.Length == 0 ? "(no id)" : g.Key).ToList();
        if (duplicated.Count > 0)
        {
            problems.Add($"bench ids missing or duplicated: {string.Join(' ', duplicated)}");
        }

        var facts = await s.CallAsync("colony-facts", NativeOps.ReadColonyFacts, new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var cooking = facts.ProductionBenches.FirstOrDefault(c => c.Bench.Bench == id && c.Cooking is RimGovernor.Brain.GameFacts.Observations.CookingProduction.Supported);
        if (cooking?.Bench.Snapshot.Token != snapshot.Token)
        {
            problems.Add($"census token {snapshot.Token} disagrees with the colony-facts cooking row {cooking?.Bench.Snapshot.Token}");
        }

        var catalog = await s.CallAsync("recipes", NativeOps.ReadRecipes, new RimGovernor.Brain.GameOrders.Observations.RecipesRequest { Scope = s.Scope, BenchId = id }, cancellationToken).ConfigureAwait(false);
        if (catalog.Snapshot.Token != snapshot.Token || catalog.Snapshot.EntityId != id)
        {
            problems.Add($"recipe catalog snapshot {catalog.Snapshot} disagrees with the census {snapshot}");
        }

        if (catalog.Recipes.Count == 0)
        {
            problems.Add("empty recipe catalog");
        }

        problems.AddRange(catalog.Recipes
            .Where(r => r.Recipe.Length == 0)
            .Select(r => $"recipe row without a def name: {r}"));

        try
        {
            var blank = await s.CallAsync("blank-bench", NativeOps.ReadBills, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = s.Scope, BenchId = " " }, cancellationToken).ConfigureAwait(false);
            problems.Add($"blank bench id: want INVALID_REQUEST, got {blank.Benches.Count} benches");
        }
        catch (NativeFailureException refused) when (refused.Value.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest)
        {
            s.Record("blank-bench", refused.Message);
        }

        try
        {
            var unknown = await s.CallAsync("unknown-bench", NativeOps.ReadRecipes, new RimGovernor.Brain.GameOrders.Observations.RecipesRequest { Scope = s.Scope, BenchId = "Thing_NoSuchBench0" }, cancellationToken).ConfigureAwait(false);
            problems.Add($"unknown bench id: want NOT_FOUND, got {unknown.Recipes.Count} recipes");
        }
        catch (NativeFailureException refused) when (refused.Value.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound)
        {
            s.Record("unknown-bench", refused.Message);
        }

        s.RecordWire("bench", row);
        s.Record("recipe_rows", catalog.Recipes.Count);
        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
