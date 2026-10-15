using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/siege-mortar</c> (Go <c>siege_mortar.go</c>) serves the routine defense planner on lab-siege: three
/// riflemen by an unroofed player mortar with HE and EMP shells, four raiders under a real LordJob_Siege camped 35 cells
/// off. The fight must answer the stationary siege with its own mortar: within the wait, its combat evidence records an
/// applied man_mortar order on the mortar and an applied counter-battery mortar_fire aimed at the camp.
/// </summary>
internal static class DefenseSiegeMortar
{
    /// <summary>Bounds the wait, in wall time on a Fast served clock, for the fight to crew the mortar and fire on the camp.</summary>
    private static readonly TimeSpan s_wait = TimeSpan.FromMinutes(4);

    /// <summary>lab-siege's camp spot north of the lab centre.</summary>
    private const int CampNorth = 20;

    /// <summary>How far from the camp, in cells, a camp aim may fall: the besiegers wander their camp while they build.</summary>
    private const int AimSlack = 12;

    /// <summary>Sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs).</summary>
    private const string LetterPauseModeTool = "test/letter_pause_mode";

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/siege-mortar",
        Scope = "A forced siege answered by mortar fire (#1208): on lab-siege, served by the routine defense planner, the fight's combat evidence records "
            + "an applied man_mortar on the colony mortar and an applied counter_battery mortar_fire aimed at the camped besiegers. Native: a live siege lord, the served fight and the native mortar ops end to end, which no colony-facts snapshot replays.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, LetterPauseModeTool],
        QuietWorld = true,
        // A checkpoint capture pauses the served game mid-fight.
        NoCheckpoint = true,
        Serve = new ServeSpec { Families = ["defense", "tend", "rescue"], ClockSpeed = "Fast", Prefix = "defense-siege-mortar" },
        Budget = AcceptanceCase.LabBudget * 2,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var staged = await CombatStage.StageAsync(s.Harness, "lab-siege", cancellationToken).ConfigureAwait(false);
        var camp = new Cell(staged.Center.X, staged.Center.Z + CampNorth);
        // A MajorThreat letter would pause the game as a player.
        var reply = await s.Harness.CallAsync("pause-mode-never", LetterPauseModeTool, new JsonObject { ["mode"] = "Never" }, cancellationToken).ConfigureAwait(false);
        if (reply["mode"].AsString() != "Never")
        {
            throw new AcceptanceException($"{LetterPauseModeTool} did not take: {reply.ToJson()}");
        }

        var process = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await process.AcquireAsync(cancellationToken).ConfigureAwait(false);
            process.KeepAuthority(cancellationToken);
            using var journal = ControllerJournal.Open(s, process, cancellationToken);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (process.Exited() is { } exited)
                {
                    throw new AcceptanceException($"service exited before the mortar fired: {exited.Message}", exited);
                }

                CombatOrderRecord? manned = null;
                CombatOrderRecord? fired = null;
                var seen = new List<CombatOrderRecord>();
                foreach (var plan in journal.Store.OpenCombatFights(cancellationToken).Keys.OrderBy(p => p.Value, StringComparer.Ordinal))
                {
                    foreach (var stop in journal.Store.CombatEvidence(plan, cancellationToken))
                    {
                        foreach (var o in stop.Orders)
                        {
                            var kind = o.Order.Kind;
                            if (kind != CombatOrderKind.ManMortar && kind != CombatOrderKind.MortarFire)
                            {
                                continue;
                            }

                            seen.Add(o);
                            if (!o.Applied)
                            {
                                continue;
                            }

                            if (kind == CombatOrderKind.ManMortar && manned is null)
                            {
                                manned = o;
                            }
                            else if (kind == CombatOrderKind.MortarFire && fired is null && o.Order.Reason == CombatOrderReason.CounterBattery && Near(o.Order.Aim, camp, AimSlack))
                            {
                                fired = o;
                            }
                        }
                    }
                }

                if (manned is not null && fired is not null)
                {
                    report["manMortar"] = OrderJson(manned);
                    report["mortarFire"] = OrderJson(fired);
                    return;
                }

                if (clock.Elapsed > s_wait)
                {
                    report["mortarOrders"] = new JsonArray([.. seen.Select(o => (JsonNode?)OrderJson(o))]);
                    throw new AcceptanceException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"within {s_wait} no applied man_mortar and counter-battery mortar_fire at the camp near {camp.X},{camp.Z} (mortar orders seen: {new JsonArray([.. seen.Select(o => (JsonNode?)OrderJson(o))]).ToJson()})"));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = await process.StopAsync().ConfigureAwait(false);
        }
    }

    private static bool Near(Cell a, Cell b, int cells)
    {
        long dx = a.X - b.X;
        long dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz) <= (long)cells * cells;
    }

    private static JsonObject OrderJson(CombatOrderRecord o) => new()
    {
        ["pawn"] = o.Order.Pawn.Value,
        ["kind"] = o.Order.Kind.ToString(),
        ["reason"] = o.Order.Reason.ToString(),
        ["aim"] = new JsonObject { ["x"] = o.Order.Aim.X, ["z"] = o.Order.Aim.Z },
        ["applied"] = o.Applied,
        ["uncertain"] = o.Uncertain,
    };
}
