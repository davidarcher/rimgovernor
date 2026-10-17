using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/stable-patient</c> (Go <c>stablepatient.go</c>): a fresh debug game seeded with the disposable
/// test/medical_management_setup fixture (two tendable Flu patients plus a fourth colonist forced into
/// GoJuiceAddiction's withdrawal stage) and test/routine_production_prepare (a pre-grown rice zone and fueled campfire
/// bill), the live service with both the tend/medical and food routine families on, and the durable store's
/// medical/critical and food/supply goal-state timelines over a wall-clock window: evidence for whether ordinary
/// triage/feeding and concurrent crop-replacement production progress together rather than one starving the other of
/// pawn time. A diagnostic, not a pass/fail gate: it fails only when the harness itself could not complete.
/// </summary>
internal static class StablePatient
{
    // The observation window; a diagnostic samples for the whole of it.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(8);

    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/stable-patient",
        Tier = CaseTier.Off,
        Scope = "Diagnostic: medical/critical (two Flu patients plus a forced GoJuiceAddiction "
            + "withdrawal patient) and food/supply (concurrent pre-seeded growing zone/campfire bill) standard-state "
            + "timelines under the live rounder/field planner, evidence for issue #1's stable-patient feeding "
            + "acceptance extension to withdrawal recovery and concurrent food production. Not a pass/fail acceptance gate.",
        Start = new FixtureStart
        {
            Op = "test/medical_management_setup",
            Args = new JsonObject { ["disease"] = true, ["failSurgery"] = false, ["manualTending"] = false, ["withdrawal"] = true },
        },

        // Feeding the patients and resting through tending are what the timeline is about.
        Keep = ["Food", "Rest"],

        // Medical: tend for the two Flu patients and the forced withdrawal patient's medical/critical Need, plus
        // medicine-reserve replenishment so tend never runs the fixture's stocked medicine dry. Food: the same
        // food/supply pipeline sustained/food exercises, to prove the pre-seeded growing zone/campfire bill keeps
        // advancing concurrently with medical dispatch.
        Serve = new ServeSpec
        {
            Families = ["tend", "medical", "field", "stockpiles", "acquisition", "cooking", "supply"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "stable-patient",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var medicalPrepared = s.Prepared ?? [];
        s.Report["medical_prepared"] = medicalPrepared.DeepClone();
        if (medicalPrepared["withdrawalPatient"].AsString().Length == 0)
        {
            throw new AcceptanceException($"medical_management_setup: missing withdrawalPatient identifier: {medicalPrepared.ToJson()}");
        }

        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = Window,
                    Concern = PolicyFunctions.CriticalCensusNeed.Value,
                    Extra = [PolicyFunctions.FoodSupplyNeed.Value],
                },
                Prepare = async (h, report, ct) =>
                {
                    var productionPrepared = await h.CallAsync("production-setup", "test/routine_production_prepare", [], ct).ConfigureAwait(false);
                    if (productionPrepared["success"].AsBool() != true)
                    {
                        throw new AcceptanceException($"routine_production_prepare refused: {productionPrepared.ToJson()}");
                    }

                    report["production_prepared"] = productionPrepared;
                },

                // Both goals' final state, side by side, for the summary.
                Audit = async (h, report, ct) =>
                {
                    var store = await ObservedSession.OpenStoreAsync(s.Config, ct).ConfigureAwait(false);
                    try
                    {
                        var food = await store.SampleNeedAsync(PolicyFunctions.FoodSupplyNeed.Value, ct).ConfigureAwait(false);
                        report["food_final"] = food;
                        var medical = await store.SampleNeedAsync(PolicyFunctions.CriticalCensusNeed.Value, ct).ConfigureAwait(false);
                        report["medical_final"] = medical;

                        // Native evidence: a critical medical Need that cannot be served should leave no tend methods
                        // at all, not eight spent on pairs the native gate refuses. The tally names how many of this
                        // goal's methods ended unsuccessful.
                        var spent = 0;
                        var failed = 0;
                        foreach (var plan in medical["plans"].Rows())
                        {
                            spent++;
                            if (plan["unsuccessful"].AsSlice().Count > 0)
                            {
                                failed++;
                            }
                        }

                        report["tend_attempts"] = new JsonObject { ["methods"] = spent, ["unsuccessful"] = failed };
                        report["concurrent_progress"] = new JsonObject
                        {
                            ["medical_methods"] = medical["method_count"].Copy(),
                            ["medical_need"] = medical["need"].Copy(),
                            ["food_methods"] = food["method_count"].Copy(),
                            ["food_need"] = food["need"].Copy(),
                        };
                    }
                    finally
                    {
                        if (store is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
