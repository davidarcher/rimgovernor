using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/food-storage-routes</c>: the forecast food read on a colony with a stockpile. The fixture sows forty ripe rice
/// plants beside one default stockpile grown through 25, 100 and 225 cells and times the read at each size for ten and
/// forty plants. The read keeps one row per pawn, plant and storage group, searching its three flood-nearest usable
/// cells, so neither rows nor searches grow with the stockpile: each row holds at most colonists x plants options and
/// plants x (colonists + 3) searches (each pawn's approach, then three cells). A unit test cannot run the pathfinder.
/// </summary>
internal static class LabFoodStorageRoutes
{
    private const string Tool = "test/food_storage_routes";

    // Flood-nearest cells the read searches per storage group (NativeFoodActivity.Nearest).
    private const int Nearest = 3;

    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/food-storage-routes",
        Scope = "Forty ripe rice plants beside a default stockpile of 25, 100 and 225 cells; at each size and for ten and forty plants the "
            + "forecast food read holds at most colonists x plants options and plants x (colonists + 3) path searches; time and allocation are reported.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [Tool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("food-storage-routes", Tool, [], ct).ConfigureAwait(false);
            s.Report["food_storage_routes"] = read.DeepClone();
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("food storage routes failed: " + read.ToJson());
            var colonists = read["colonists"]!.GetValue<int>();
            var over = new List<string>();
            foreach (var row in read["rows"]!.AsArray().OfType<JsonObject>())
            {
                int cells = row["cells"]!.GetValue<int>(), plants = row["plants"]!.GetValue<int>();
                int options = row["options"]!.GetValue<int>(), searches = row["searches"]!.GetValue<int>();
                if (row["known"]?.GetValue<bool>() != true || options == 0) over.Add($"{cells} cells x {plants} plants: no options");
                if (options > colonists * plants) over.Add($"{cells} cells x {plants} plants: {options} options > {colonists * plants}");
                if (searches > plants * (colonists + Nearest)) over.Add($"{cells} cells x {plants} plants: {searches} searches > {plants * (colonists + Nearest)}");
            }

            if (over.Count != 0) throw new AcceptanceException("food storage routes: " + string.Join("; ", over));
        },
    };
}
