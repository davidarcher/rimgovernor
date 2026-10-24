using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// Explicit selected intents isolate continuation tests from portfolio valuation.
// Generation and selection are exercised by the coupled portfolio tests.
internal static class ActivityContinuationFixtures
{
    /// <summary>A coupled capture's research section with no researcher at a bench.</summary>
    internal static ResearchActivityRead NoResearch => new() { Workers = [], Findings = [] };

    internal static PawnActivities Harvest(string pawn = "pawn", string? helper = null, long deadline = 500)
    {
        var worker = new SelectedActivityTask("harvest", pawn, [],
            new(PawnActivityKind.Work, "crop", "Harvest", "Harvest", 0, deadline), new(10, 10), 100);
        var delivery = new SelectedActivityTask("delivery", helper ?? pawn, [worker.Id],
            new(PawnActivityKind.Deliver, "crop", "", "", 8, deadline) { Destination = new(30, 10) }, new(10, 10), 100)
            { Resource = "Rice", Source = new(10, 10) };
        var order = new PawnActivityOrder(pawn, "method/" + pawn, "method", 0, 1, false, [worker.Step]) { Task = worker.Id };
        return new(1, helper is null ? [order] : [order,
            new(helper, "method/" + helper, "method", 0, 1, false,
                [new(PawnActivityKind.Approach, "", "", "", 0, deadline) { Destination = delivery.Location },
                 new(PawnActivityKind.Hold, "", "", "", 0, deadline)]) { Task = delivery.Id, Preparing = true }])
            { Tasks = [worker, delivery] };
    }

    internal static PawnActivities Cooking()
    {
        var supply = new SelectedActivityTask("supply", "pawn", [],
            new(PawnActivityKind.Deliver, "raw", "", "", 10, 500) { Destination = new(5, 6) }, new(3, 4), 100)
            { Resource = "Rice", Source = new(3, 4) };
        var cook = new SelectedActivityTask("cook", "pawn", [supply.Id],
            new(PawnActivityKind.Recipe, "bench", "cook-giver", "", 0, 500)
                { Recipe = new("bill", "recipe", [new("raw", 10)]) { Resource = "Rice" } }, new(5, 5), 101);
        var delivery = new SelectedActivityTask("delivery", "pawn", [cook.Id],
            new(PawnActivityKind.Deliver, "observed-output", "", "", 1, 500) { Destination = new(8, 9) }, new(5, 5), 102)
            { Resource = "Meal", Source = new(5, 5) };
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false, [supply.Step]) { Task = supply.Id }])
            { Tasks = [supply, cook, delivery] };
    }
}
