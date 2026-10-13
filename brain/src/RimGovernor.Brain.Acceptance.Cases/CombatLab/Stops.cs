using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/stops</c> (Go <c>stops.go</c>).</summary>
internal static class CombatLabStops
{
    // stopsFirstWindow bounds the window the raiders close in: 20 cells of walking is well under this at Superfast.
    private const int FirstWindow = 2000;

    // stopsBackstop is the combat backstop: a window with no armed event stops on its budget.
    private const int Backstop = 300;

    // stopsWindows bounds the fight after contact: 20 backstops.
    private const int Windows = 20;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/stops",
        Scope = "Event-triggered combat stops (#849): on lab-open a combat window armed with downed and entered-range stops on the exact tick a raider's weapon reaches a colonist or a colonist's reaches a raider (the stop event's tick equals its occurrence tick), once per raider and direction per combat, later windows without an event stop on the 300-tick backstop, and a colonist going down stops on its tick.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken).ConfigureAwait(false);
        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "combat-stops-acquire", identity, cancellationToken).ConfigureAwait(false);
        var report = s.Report;
        var clock = new ScenarioClock(h.WireFunc(), identity, Draft.Controller, new Report())
        {
            Grant = grant,

            // Acknowledged, the raiders' approach is not a hostile stop; the health thresholds are off so only armed
            // events and the budget stop.
            CombatTargets = staged.Hostiles(),
            PolicyEdit = policy =>
            {
                policy["combatStopEvents"] = new JsonArray("COMBAT_EVENT_DOWNED", "COMBAT_EVENT_ENTERED_RANGE");
                policy["healthDropFraction"] = 1;
                policy["minHealthFraction"] = 0.01;
            },
        };
        var stops = new JsonArray();
        var sawRange = false;
        var sawBackstop = false;
        var sawDowned = false;
        var entered = new HashSet<string>(StringComparer.Ordinal);
        for (var window = 0; window <= Windows && !sawDowned; window++)
        {
            ulong budget = Backstop;
            if (window == 0)
            {
                budget = FirstWindow;
            }

            JsonObject started;
            try
            {
                started = await clock.ChangeAsync("Superfast", budget, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"window {CaseSupport.Tag(window)} start: {e.Message}", e);
            }

            clock.SeekEvents(started);
            JsonObject stop;
            try
            {
                stop = await AwaitStopAsync(clock, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"window {CaseSupport.Tag(window)}: {e.Message}", e);
            }

            var startTick = started["startTick"].AsUInt64() ?? 0;
            stop["window"] = window;
            stop["startTick"] = startTick;
            stops.Add(stop.DeepClone());
            report["stops"] = stops;
            var tick = stop["tick"].AsUInt64() ?? 0;
            switch (stop["kind"].AsString())
            {
                case "combat_event":
                    if (tick != (stop["occurrenceTick"].AsUInt64() ?? 0) || tick != (stop["detectedTick"].AsUInt64() ?? 0))
                    {
                        throw new AcceptanceException($"window {CaseSupport.Tag(window)}: combat stop off its event's tick: {stop.ToJson()}");
                    }

                    switch (stop["event"].AsString())
                    {
                        case "COMBAT_EVENT_ENTERED_RANGE":
                            // First time only: the combat remembers a raider across windows. Keyed by hostile and
                            // direction ("reaches" or "reached by").
                            var key = stop["thingId"].AsString() + (stop["detail"].AsString().Contains("reached by", StringComparison.Ordinal) ? "true" : "false");
                            if (!entered.Add(key))
                            {
                                throw new AcceptanceException($"window {CaseSupport.Tag(window)}: {key} entered range a second time: {stop.ToJson()}");
                            }

                            sawRange = true;
                            break;
                        case "COMBAT_EVENT_DOWNED":
                            sawDowned = true;
                            break;
                        default:
                            throw new AcceptanceException($"window {CaseSupport.Tag(window)}: unarmed combat event stopped the clock: {stop.ToJson()}");
                    }

                    break;
                case "tick_budget":
                    if (window == 0)
                    {
                        throw new AcceptanceException($"raiders never came within weapon range in {CaseSupport.Tag(FirstWindow)} ticks: {stop.ToJson()}");
                    }

                    if (tick - startTick != Backstop)
                    {
                        throw new AcceptanceException($"window {CaseSupport.Tag(window)}: backstop at {CaseSupport.Tag((long)(tick - startTick))} ticks, want {CaseSupport.Tag(Backstop)}: {stop.ToJson()}");
                    }

                    sawBackstop = true;
                    break;
                default:
                    throw new AcceptanceException($"window {CaseSupport.Tag(window)}: unexpected stop: {stop.ToJson()}");
            }

            if (window == 0 && !sawRange)
            {
                throw new AcceptanceException($"first window stopped on {stop.ToJson()} before entered range");
            }
        }

        report["enteredRange"] = sawRange;
        report["backstop"] = sawBackstop;
        report["downed"] = sawDowned;
        if (!sawBackstop && !sawDowned)
        {
            throw new AcceptanceException($"neither a backstop nor a downed stop after contact: {stops.ToJson()}");
        }
    }

    // awaitStop polls until the running window stops, then returns its stop event: kind, the armed event, and the
    // event's context, detected and occurrence ticks.
    private static async Task<JsonObject> AwaitStopAsync(ScenarioClock clock, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var status = await clock.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
            if (status["stopReason"].AsString().Length != 0)
            {
                var events = await clock.PollAsync(cancellationToken).ConfigureAwait(false);
                foreach (var e in events)
                {
                    var row = e.AsMap() ?? [];
                    var native = row["native"].AsMap() ?? [];
                    if (native["stopped"].AsMap() is not { } stopped)
                    {
                        continue;
                    }

                    var output = new JsonObject { ["kind"] = row["kind"]?.DeepClone(), ["detail"] = native["detail"]?.DeepClone() };
                    var contextRow = native["context"].AsMap() ?? [];
                    output["tick"] = contextRow["tick"].AsUInt64() ?? 0;
                    output["detectedTick"] = stopped["detectedTick"].AsUInt64() ?? 0;
                    output["occurrenceTick"] = stopped["occurrenceTick"].AsUInt64() ?? 0;
                    if (stopped["combat"].AsMap() is { } combat)
                    {
                        output["event"] = combat["event"].AsString();
                        output["thingId"] = combat["thingId"].AsString();
                    }

                    return output;
                }

                throw new AcceptanceException($"stopped ({status["stopReason"].ToJson()}) with no stop event");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new AcceptanceException("window did not stop in 90 s");
    }
}
