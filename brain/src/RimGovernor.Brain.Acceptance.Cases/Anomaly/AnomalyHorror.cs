using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>anomaly/horror-incident</c> (Go <c>anomaly/horror.go</c>) proves a colony survives a horror incident: the game's own
/// Anomaly threat incident arrives at the staged colony, the served defense planner answers it with combat tactics, and
/// natively every colonist is alive and no arrival is left on its feet.
/// </summary>
internal static class AnomalyHorror
{
    private const string IncidentTool = "test/horror_incident";
    private const string InspectTool = "test/horror_inspect";

    // Sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs): a threat letter would pause the served game.
    private const string LetterPauseModeTool = "test/letter_pause_mode";

    // Bounds the journal wait in wall time on a Fast served clock; the stall budget ends it earlier when nothing moves
    // and FightTicks is the game-time bound.
    private static readonly TimeSpan FightCeiling = TimeSpan.FromMinutes(10);
    private const ulong FightTicks = Wait.TicksPerDay;

    internal static AcceptanceCase Case() => new()
    {
        Name = "anomaly/horror-incident",
        Scope = "Issue #1748: a colony of four riflemen behind a sandbag line survives an Anomaly threat incident: the game's own incident "
            + "worker fires an incident whose arrivals are a melee entity pack, the served defense planner answers it through the combat "
            + "tactics of #1739, and after the fight every colonist is alive and no arrival is up and on the map (dead, downed or gone). "
            + "A Go snapshot over recorded facts cannot cover it: the incident worker's arrivals, the entities' own AI and attacks, the "
            + "colonists' drafted fire and the deaths are vanilla's, asserted on the live pawns; the tactic choice itself is "
            + "policy/combat_anomaly_test.go. The fixture picks the incident from the game's defs by what it spawns, not by name.",
        Start = new LabStart { Colonists = 4 },
        Expansions = ["ludeon.rimworld.anomaly"],
        NoKeep = true,
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, LetterPauseModeTool, IncidentTool, InspectTool],
        QuietWorld = true,

        // A checkpoint capture pauses the served game mid-fight.
        NoCheckpoint = true,
        Serve = new ServeSpec
        {
            Families = ["defense", "tend", "rescue"],
            ClockSpeed = "Fast",
            Prefix = "anomaly-horror-incident",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    internal static List<string> IdList(JsonNode? raw)
    {
        var output = new List<string>();
        foreach (var v in raw.AsSlice())
        {
            var id = v.AsString();
            if (id.Length > 0)
            {
                output.Add(id);
            }
        }

        return output;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        _ = await CombatStage.StageAsync(h, "lab-horror", cancellationToken).ConfigureAwait(false);
        var pauseMode = await h.CallAsync("pause-mode-never", LetterPauseModeTool, new JsonObject { ["mode"] = "Never" }, cancellationToken).ConfigureAwait(false);
        if (pauseMode["mode"].AsString() != "Never")
        {
            throw new AcceptanceException($"{LetterPauseModeTool} did not take: {pauseMode.ToJson()}");
        }

        var incident = await h.CallAsync("horror-incident", IncidentTool, null, cancellationToken).ConfigureAwait(false);
        if (incident["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{IncidentTool} refused: {incident.ToJson()}");
        }

        var colonists = IdList(incident["colonistIds"]);
        var threats = IdList(incident["threatIds"]);
        if (colonists.Count != 4 || threats.Count == 0)
        {
            throw new AcceptanceException($"fixture: unexpected horror staging: {incident.ToJson()}");
        }

        report["incident"] = incident["incident"]?.DeepClone();
        report["fixture"] = incident.DeepClone();

        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var store = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            try
            {
                // The colony's answer is journalled: the ActiveCombat goal binds when the arrivals are seen and recovers
                // once no live hostile is left. The native outcome is read after the service releases the game slot.
                var sawFight = false;
                try
                {
                    await new Wait
                    {
                        Ceiling = FightCeiling,
                        Stall = Wait.StallBudget(),
                        Terminal = service.Exited,
                        Ticks = FightTicks,
                        Tick = async ct =>
                        {
                            var review = await ServiceJournal.TryRoundsAsync(store, ct).ConfigureAwait(false) ?? throw new AcceptanceException("no persisted review");
                            return (ulong)review.Tick.Value;
                        },
                    }.WaitProgressAsync(
                        async ct =>
                        {
                            // Go fightState: an unreadable review reads as no binding.
                            var review = await ServiceJournal.TryRoundsAsync(store, ct).ConfigureAwait(false);
                            var binding = review?.Incident(PolicyFunctions.ActiveCombat);
                            var bound = binding is not null;
                            var need = binding is { } b ? Wire.Name(b.Situation) : "";
                            var tick = (ulong)(review?.Tick.Value ?? 0);
                            sawFight = sawFight || bound;
                            var resolved = sawFight && (!bound || binding!.Situation == Situation.Clear);
                            report["combat_bound"] = bound;
                            report["combat_need"] = need;
                            report["saw_fight"] = sawFight;
                            return (Wait.Signature(bound, need, tick / (Wait.TicksPerDay / 24)), resolved);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"combat answer: {e.Message}", e);
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

        // Native postconditions: every colonist alive, every arrival dead, downed or gone from the map.
        h = s.Reattach();
        var reply = await h.CallAsync(
            "horror-inspect",
            InspectTool,
            new JsonObject { ["colonistIds"] = string.Join(',', colonists), ["threatIds"] = string.Join(',', threats) },
            cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{InspectTool} refused: {reply.ToJson()}");
        }

        report["inspect"] = reply.DeepClone();
        foreach (var raw in reply["colonists"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["found"].AsBool() != true)
            {
                throw new AcceptanceException($"colonist {row["id"].ToJson()} is gone: {row.ToJson()}");
            }

            if (row["dead"].AsBool() == true)
            {
                throw new AcceptanceException($"colonist {row["id"].ToJson()} died in the horror incident: {row.ToJson()}");
            }
        }

        foreach (var raw in reply["threats"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["found"].AsBool() != true)
            {
                continue;
            }

            var dead = row["dead"].AsBool() == true;
            var destroyed = row["destroyed"].AsBool() == true;
            var spawned = row["spawned"].AsBool() == true;
            var downed = row["downed"].AsBool() == true;
            if (!dead && !destroyed && spawned && !downed)
            {
                throw new AcceptanceException($"arrival {row["id"].ToJson()} is still up on the map after the fight: {row.ToJson()}");
            }
        }
    }
}
