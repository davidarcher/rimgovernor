using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/browse</c> (Go <c>browse.go</c>): a negotiator opens a session to inspect priced goods. The journal must
/// show an open and a terminal phase; silver can leave only through an accepted trade, so this case exercises reading
/// and cancelling without trading.
/// </summary>
internal static class TradeBrowse
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/browse",
        Scope = "Caravan offer records (#2168): the negotiator's session reads the caravan's priced goods, and the planner " +
            "either buys the staged lines or cancels without trading. A Go snapshot test cannot cover it: the open, " +
            "sheet read and cancel are native operations whose absence of side effects only a live session shows.",
        Start = TradeRounds.RoutineFixture(),
        Serve = new ServeSpec { Families = ["trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "trade-browse" },
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

        var deficit = false;
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TradeRounds.Window,
                    Concern = PolicyFunctions.TradeWithCaravan.Value,
                    Until = TradeRounds.SettledAfterDeficit(() => deficit = true, () => deficit),
                },
                Audit = async (h, report, ct) =>
                {
                    // Go read the bound occurrence's methods off the rounds record; the incident-shaped sample reads
                    // the same occurrence's committed methods (SampleStandard on an incident kind).
                    var store = await ObservedSession.OpenStoreAsync(s.Config, ct).ConfigureAwait(false);
                    JsonObject goal;
                    try
                    {
                        goal = await store.SampleStandardAsync(PolicyFunctions.TradeWithCaravan.Value, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (store is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    if (!Flag(goal["concern_bound"]))
                    {
                        throw new AcceptanceException("TradeWithCaravan never bound an occurrence");
                    }

                    var phases = new Dictionary<string, bool>(StringComparer.Ordinal) { ["open"] = false, ["accept"] = false, ["end"] = false };
                    foreach (var plan in goal["plans"].AsSlice())
                    {
                        var method = plan.AsMap()?["method"].AsString() ?? "";
                        foreach (var kind in phases.Keys.ToArray())
                        {
                            phases[kind] = phases[kind] || method.StartsWith("trade-" + kind + "-", StringComparison.Ordinal);
                        }
                    }

                    var recorded = new JsonObject();
                    foreach (var (kind, seen) in phases)
                    {
                        recorded[kind] = seen;
                    }

                    report["trade_phases"] = recorded;
                    if (!phases["open"])
                    {
                        throw new AcceptanceException($"no session was opened for the caravan: {recorded.ToJson()}");
                    }

                    if (!phases["accept"] && !phases["end"])
                    {
                        throw new AcceptanceException($"the opened session never ended: {recorded.ToJson()}");
                    }

                    var resources = await TradeRounds.ResourceCensusAsync(h, ct).ConfigureAwait(false);
                    var silver = resources.GetValueOrDefault("Silver");
                    report["trade_silver"] = new JsonObject { ["silver"] = silver, ["silver_spawned"] = TradeRounds.Silver };
                    if (!phases["accept"] && silver != TradeRounds.Silver)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a cancelled session moved silver: {silver} of {TradeRounds.Silver}"));
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
