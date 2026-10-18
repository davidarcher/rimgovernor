using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/food-storage-routes</c>: the forecast food read on a colony with a stockpile. test/food_storage_routes is a
/// benchmark the stage primitives cannot express: it sows forty ripe rice plants beside one default stockpile grown through
/// 25, 100 and 225 cells and reads it at each size for ten and forty plants. The read keeps one row per pawn, plant
/// and storage group, searching its three flood-nearest usable cells, so neither rows nor searches grow with the
/// stockpile: each row holds at most colonists x plants options and plants x (colonists + 3) searches (each pawn's
/// approach, then three cells). A unit test cannot run the pathfinder.
/// </summary>
internal static class LabFoodStorageRoutes
{
    private const string Tool = "test/food_storage_routes";

    // Flood-nearest cells the read searches per storage group (NativeFoodActivity.Nearest).
    private const int Nearest = 3;

    private static readonly FixtureOp<NoArgs, Fx.FoodStorageRoutesRead> Routes = ServedOps.Read<Fx.FoodStorageRoutesRead>(Tool);

    internal static TypedCase Case() => new()
    {
        Name = "lab/food-storage-routes",
        Scope = "Forty ripe rice plants beside a default stockpile of 25, 100 and 225 cells; at each size and for ten and forty plants the "
            + "forecast food read holds at most colonists x plants options and plants x (colonists + 3) path searches.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Ops = [Tool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var read = await s.FixtureAsync("food-storage-routes", Routes, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("food_storage_routes", ServedOps.Json(read));
            var colonists = read.Colonists;
            var over = new List<string>();
            if (read.Rows.Count != 6)
            {
                over.Add($"{read.Rows.Count} rows, want 6");
            }

            foreach (var row in read.Rows)
            {
                var at = $"{row.Cells} cells x {row.Plants} plants";
                if (row.Options == 0)
                {
                    over.Add($"{at}: no options");
                }

                if (row.Options > colonists * row.Plants)
                {
                    over.Add($"{at}: {row.Options} options > {colonists * row.Plants}");
                }

                if (row.Searches > row.Plants * (colonists + Nearest))
                {
                    over.Add($"{at}: {row.Searches} searches > {row.Plants * (colonists + Nearest)}");
                }
            }

            if (over.Count != 0)
            {
                throw new AcceptanceException("food storage routes: " + string.Join("; ", over));
            }
        },
    };
}
