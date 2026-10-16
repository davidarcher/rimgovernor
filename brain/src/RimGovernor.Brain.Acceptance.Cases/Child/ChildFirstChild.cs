using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>child/first-child</c> (Go <c>first_child.go</c>) proves a Biotech colony raises its first child end to end in
/// vanilla: a baby with no breastfeeder in the colony is fed through the baby food the food/baby-feeding Need has the
/// colony cook, and grows into the Child developmental stage alive and not starved.
/// </summary>
internal static class ChildFirstChild
{
    private const string PrepareTool = "test/baby_care_prepare";
    private const string InspectTool = "test/baby_care_inspect";

    /// <summary>Bounds the journal wait for the baby food bill; the stall budget ends it earlier when nothing moves.</summary>
    private static readonly TimeSpan BillCeiling = TimeSpan.FromMinutes(6);

    /// <summary>Bounds the native run after the service stops: the fixture leaves the baby two game days short of the Child stage (120000 ticks).</summary>
    private const ulong GrowTicks = 150000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "child/first-child",
        Scope = "Issue #1691: with no breastfeeder in the colony, the food/baby-feeding Need has the colony cook baby-edible food "
            + "and the baby is fed by it until it reaches the Child developmental stage. A Go snapshot over recorded facts cannot "
            + "cover it: the chain is vanilla's own (bottle-feeding on Childcare work, the baby's food need, ageing and the "
            + "stage change), asserted on the live pawn.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.biotech"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, InspectTool],

        // The shelter family keeps supervised windows running (the bill planner alone never advances the clock); dialog
        // answers the letters.
        Serve = new ServeSpec
        {
            Families = ["bill", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "child-first-child",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>Whether the journal holds a completed bill for a baby-edible recipe on the fixture's stove; the planner picks among the recipes, so any counts.</summary>
    private static async Task<bool> BabyFoodBillPlacedAsync(Journal journal, string bench, List<string> recipes, CancellationToken cancellationToken)
    {
        foreach (var plan in await ServiceJournal.PlanHistoryAsync(journal, "bill-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.ProductionBill is { } bill && bill.Bench == bench && recipes.Contains(bill.Recipe, StringComparer.Ordinal) && progress.View.Stage == Stage.Completed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var baby = prepared["babyId"].AsString();
        var bench = prepared["benchId"].AsString();
        var recipes = prepared["recipes"].AsSlice().Select(r => r.AsString()).ToList();
        var stagedFood = prepared["foodLevel"].AsNumber();
        if (baby.Length == 0 || bench.Length == 0 || recipes.Count == 0 || stagedFood <= 0)
        {
            throw new AcceptanceException($"fixture: unexpected first-child staging: {prepared.ToJson()}");
        }

        report["baby"] = baby;
        report["bench"] = bench;
        report["recipes"] = new JsonArray([.. recipes.Select(r => (JsonNode?)r)]);
        report["fixture"] = prepared.DeepClone();

        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var store = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            try
            {
                // The colony's own answer to a baby it cannot breastfeed: the standing baby food bill on its stove.
                try
                {
                    await new Wait { Ceiling = BillCeiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                        async ct =>
                        {
                            var placed = await BabyFoodBillPlacedAsync(store, bench, recipes, ct).ConfigureAwait(false);
                            return (Wait.Signature(placed), placed);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"baby food bill: {e.Message}", e);
                }
            }
            finally
            {
                store.Dispose();
            }

            report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        // Native postconditions after the service releases the game slot: the bill stands on the stove for a baby-edible
        // recipe, and the baby is fed (its food level is above the staged level, which only ingestion raises; the service
        // run may already have carried it past the Child stage) and reaches the Child stage alive and not starved.
        var h = s.Reattach();
        var args = new JsonObject { ["babyId"] = baby, ["benchId"] = bench };
        JsonObject? last = null;
        var fed = false;
        ulong elapsed = 0;
        try
        {
            elapsed = await RunAt.RunUntilAsync(
                h,
                "child-grown",
                GrowTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var reply = await h.CallAsync("baby-inspect", InspectTool, args, ct).ConfigureAwait(false);
                    if (reply["success"].AsBool() != true)
                    {
                        throw new AcceptanceException($"baby inspect refused: {reply.ToJson()}");
                    }

                    last = reply;
                    if (reply["dead"].AsBool() == true)
                    {
                        throw new AcceptanceException($"the baby died: {reply.ToJson()}");
                    }

                    var food = reply["foodLevel"].AsNumber();
                    fed = fed || food > stagedFood;
                    return (Wait.Signature(reply["stage"].ToJson(), food, fed), reply["stage"].AsString() == "Child");
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            report["grow_ticks"] = elapsed;
            report["inspect"] = last?.DeepClone();
            report["fed"] = fed;
        }

        var billed = last is not null && last["bills"].AsSlice().Any(raw =>
            raw.AsMap() is { } row && row["babyEdible"].AsBool() == true && recipes.Contains(row["recipe"].AsString(), StringComparer.Ordinal));
        if (!billed)
        {
            throw new AcceptanceException($"no baby food bill ({string.Join(", ", recipes)}) stands on bench {bench}: {last?["bills"].ToJson() ?? "null"}");
        }

        if (!fed)
        {
            throw new AcceptanceException($"the baby's food level never rose above its staged {stagedFood}, so it was never fed: {last.ToJson()}");
        }

        var severity = last!["malnutrition"].AsNumber();
        if (severity > 0)
        {
            throw new AcceptanceException($"the baby reached the Child stage malnourished (severity {severity}): {last.ToJson()}");
        }
    }
}
