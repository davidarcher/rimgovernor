using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/routine</c> (Go <c>rounds.go</c>): native negotiation and acceptance with a medicine deficit, silver and an
/// arriving caravan. The live service keeps the Concern open during arrival, negotiates, stages an affordable purchase
/// and accepts it; the native resource census must show gained medicine and spent silver.
/// </summary>
internal static class TradeRounds
{
    /// <summary>Bounds the watch: the caravan's walk in, the negotiator's walk and the three session phases each land in the stop between windows.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The silver stack the fixture spawns (under the 500 stack limit); the medicine buy must come out of it.</summary>
    internal const int Silver = 400;

    private static readonly string[] Medicines = ["MedicineUltratech", "MedicineIndustrial", "MedicineHerbal"];

    /// <summary>The fixture both serve-driven trade cases start from.</summary>
    internal static FixtureStart RoutineFixture() => new()
    {
        On = new LabStart(),
        Op = "test/trade_fixture",
        Args = new JsonObject { ["action"] = "routine_setup", ["silver"] = Silver, ["medicine"] = 30 },
    };

    /// <summary>
    /// The watch's early exit shared by browse and routine: the goal recovering after a measured deficit is the trade
    /// settling (an accept, or the need gone); the audit says which.
    /// </summary>
    internal static Func<JsonObject, bool> SettledAfterDeficit(Action markDeficit, Func<bool> deficit) => sample =>
    {
        if (!Flag(sample["concern_bound"]))
        {
            return false;
        }

        var need = sample["need"].AsString();
        if (need == "unmet")
        {
            markDeficit();
            return false;
        }

        return deficit() && need == "met";
    };

    /// <summary>The native resource census by definition name, units summed (an item the census holds none of has no row at all).</summary>
    internal static async Task<Dictionary<string, double>> ResourceCensusAsync(Harness h, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, "audit-identity", cancellationToken).ConfigureAwait(false);
        var facts = await TradeKit.ObserveAsync(h, "audit-colony-facts", "observations_read_colony_facts", identity, null, cancellationToken).ConfigureAwait(false);
        var resources = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var raw in facts["resources"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var name = row["defName"].AsString();
            resources[name] = resources.GetValueOrDefault(name) + Num(row["units"]);
        }

        return resources;
    }

    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/routine",
        Scope = "TradeWithCaravan (#234): an arriving caravan sells the observed medicine shortfall " +
            "from observed silver through the routine trade family alone -- walked open, staged lines, " +
            "accept -- proven by the native resource census after the service stops.",
        Start = RoutineFixture(),

        // Every need frozen: the negotiator's walks are the only pawn time the case is about.
        Serve = new ServeSpec { Families = ["trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "trade-routine" },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        s.Report["trade_prepared"] = prepared.DeepClone();
        if (!Flag(prepared["success"]))
        {
            throw new AcceptanceException($"routine_setup: no trader caravan spawned: {prepared.ToJson()}");
        }

        if (Num(prepared["colonyMedicine"]) != 0)
        {
            throw new AcceptanceException($"routine_setup: colony medicine remains: {prepared.ToJson()}");
        }

        foreach (var row in prepared["stocked"].AsSlice())
        {
            var stock = row.AsMap() ?? throw new AcceptanceException($"routine_setup: unreadable stock row: {row.ToJson()}");
            if (!Flag(stock["listed"]))
            {
                throw new AcceptanceException($"routine_setup: the caravan does not list the stocked medicine: {row.ToJson()}");
            }
        }

        var deficit = false;
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = Window,
                    Concern = PolicyFunctions.TradeWithCaravan.Value,
                    Until = SettledAfterDeficit(() => deficit = true, () => deficit),
                },
                Audit = async (h, report, ct) =>
                {
                    var store = await ObservedSession.OpenStoreAsync(s.Config, ct).ConfigureAwait(false);
                    try
                    {
                        report["trade_final"] = await store.SampleStandardAsync(PolicyFunctions.TradeWithCaravan.Value, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (store is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    var resources = await ResourceCensusAsync(h, ct).ConfigureAwait(false);

                    // The Core medicines the lab's trader carries.
                    var medicine = 0.0;
                    foreach (var name in Medicines)
                    {
                        medicine += resources.GetValueOrDefault(name);
                    }

                    var silver = resources.GetValueOrDefault("Silver");
                    report["trade_census"] = new JsonObject { ["medicine"] = medicine, ["silver"] = silver, ["silver_spawned"] = Silver };
                    if (!deficit)
                    {
                        throw new AcceptanceException("TradeWithCaravan never measured a deficit with the caravan present");
                    }

                    if (medicine <= 0)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no medicine was bought: census medicine {medicine}, silver {silver} of {Silver}"));
                    }

                    if (silver >= Silver)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"medicine appeared without silver leaving: census medicine {medicine}, silver {silver} of {Silver}"));
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
