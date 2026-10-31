using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

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
            new PawnActivityStep(PawnActivityKind.Deliver, "crop", "", "", 8, deadline).At(new(30, 10)), new(10, 10), 100)
            { Resource = "Rice", Source = new(10, 10) };
        var order = new PawnActivityOrder(pawn, "method/" + pawn, "method", 0, 1, false, [worker.Step]) { Task = worker.Id };
        return new(1, helper is null ? [order] : [order,
            new(helper, "method/" + helper, "method", 0, 1, false,
                [new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, deadline).At(delivery.Location),
                 new(PawnActivityKind.Hold, "", "", "", 0, deadline)]) { Task = delivery.Id, Preparing = true }])
            { Tasks = [worker, delivery] };
    }

    /// <summary>A re-admitted ingredient delivery to a stalled bill's bench (task 5.3): the bill itself runs natively, so the
    /// batch holds the delivery alone.</summary>
    internal static PawnActivities IngredientDelivery()
    {
        var supply = new SelectedActivityTask("supply", "pawn", [],
            new PawnActivityStep(PawnActivityKind.Deliver, "raw", "", "", 10, 500).At(new(5, 6)), new(3, 4), 100)
            { Resource = "Rice", Source = new(3, 4) };
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false, [supply.Step]) { Task = supply.Id }])
            { Tasks = [supply] };
    }
}
