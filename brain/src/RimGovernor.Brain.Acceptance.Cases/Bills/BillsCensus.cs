using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

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
        var bills = await s.CallAsync("bills", NativeOps.ReadBills, new Observations.BillsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var census = TypedReads.Observed("bills", NativeOps.ReadBills, bills, bills.Outcome?.Observed);
        var rows = census.Benches.Where(b => b.Bench?.Id == id).ToList();
        if (rows is not [{ Snapshot: { Token.Length: > 0 } snapshot } row] || snapshot.EntityId != id)
        {
            throw new AcceptanceException($"bill census: want the staged bench {id} once with a CAS token, got {rows.Count} rows: {string.Join("; ", rows)}");
        }

        var duplicated = census.Benches.GroupBy(b => b.Bench?.Id).Where(g => g.Key is null || g.Count() > 1).Select(g => g.Key ?? "(no id)").ToList();
        if (duplicated.Count > 0)
        {
            problems.Add($"bench ids missing or duplicated: {string.Join(' ', duplicated)}");
        }

        var facts = await s.CallAsync("colony-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        var cooking = facts.Cooking.FirstOrDefault(c => c.Bench?.Id == id);
        if (cooking?.BenchSnapshot?.Token != snapshot.Token)
        {
            problems.Add($"census token {snapshot.Token} disagrees with the colony-facts cooking row {cooking?.BenchSnapshot?.Token}");
        }

        var recipes = await s.CallAsync("recipes", NativeOps.ReadRecipes, new Observations.RecipesRequest { Scope = s.Scope, BenchId = id }, cancellationToken).ConfigureAwait(false);
        var catalog = TypedReads.Observed("recipes", NativeOps.ReadRecipes, recipes, recipes.Outcome?.Observed);
        if (catalog.Snapshot?.Token != snapshot.Token || catalog.Snapshot?.EntityId != id)
        {
            problems.Add($"recipe catalog snapshot {catalog.Snapshot} disagrees with the census {snapshot}");
        }

        if (catalog.Recipes.Count == 0)
        {
            problems.Add("empty recipe catalog");
        }

        problems.AddRange(catalog.Recipes
            .Where(r => r.Recipe?.DefName is null or "" || r.AvailableNow is null || r.AvailableOnBench is null)
            .Select(r => $"recipe row without a def name or availability: {r}"));

        var blank = await s.CallAsync("blank-bench", NativeOps.ReadBills, new Observations.BillsRequest { Scope = s.Scope, BenchId = " " }, cancellationToken).ConfigureAwait(false);
        if (blank.Outcome?.Failure?.Code != Common.FailureCode.InvalidRequest)
        {
            problems.Add($"blank bench id: want INVALID_REQUEST, got {blank.Outcome}");
        }

        var unknown = await s.CallAsync("unknown-bench", NativeOps.ReadRecipes, new Observations.RecipesRequest { Scope = s.Scope, BenchId = "Thing_NoSuchBench0" }, cancellationToken).ConfigureAwait(false);
        if (unknown.Outcome?.Failure?.Code != Common.FailureCode.NotFound)
        {
            problems.Add($"unknown bench id: want NOT_FOUND, got {unknown.Outcome}");
        }

        s.RecordWire("bench", row);
        s.Record("recipe_rows", catalog.Recipes.Count);
        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
