using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRounds = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The sheltering cases (Go <c>shelter/sheltering.go</c>) prove MaintainShelter's Safe area and the sheltering planner end to
/// end on the lab: the ShelterFixture hut at the centre is the only roofed room, so the Safe area covers it. The assertion is
/// the native allowed-area writes landing on colonists and a tame animal, the Safe area the game holds and the pawns
/// physically sheltering from a real condition or a real manhunter pack. The controller holds the GABP slot while it runs, so
/// the trigger ends between two service runs: the planner keeps no history, so the restarted service restores from the
/// facts alone.
/// </summary>
internal static class ShelterSheltering
{
    private static readonly string[] ShelteringFamilies = ["recovery", "sheltering"];

    private static readonly TimeSpan ShelterWait = TimeSpan.FromMinutes(4);

    /// <summary>The game-time budget for the walk from the staging row (ten cells from the hut) into it.</summary>
    private const ulong ShelterWalkTicks = 6000;

    private sealed record Kind(string Name, string Trigger, string Scope, bool Animal);

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        Kind[] kinds =
        [
            new(
                "shelter/manhunter_waitout",
                "manhunter",
                "A manhunter pack enters the lab from the west edge: the colonists, none drafted, are moved into the " +
                "Safe area (the roofed hut) and no one is injured; once the pack has left the map, everyone still " +
                "restricted to the Safe area is unrestricted again. Native area writes, the native Safe area and a " +
                "real manhunter pack are the signal, not a planner decision.",
                false),
            new(
                "shelter/toxic_fallout",
                "fallout",
                "Toxic fallout starts on the lab: every colonist and the tame pen-free dog are moved into the Safe area " +
                "(the roofed hut) and stand in it; once the fallout ends, all of them are unrestricted again. Native " +
                "area writes, the native Safe area and a real game condition are the signal, not a planner decision.",
                true),
        ];
        foreach (var c in kinds)
        {
            yield return new AcceptanceCase
            {
                Name = c.Name,
                Scope = c.Scope,
                Start = new FixtureStart { On = new LabStart(), Op = "test/shelter_prepare", Args = new JsonObject { ["trigger"] = c.Trigger } },
                RequiredOps = ["test/shelter_prepare", "test/shelter_stage"],
                Serve = new ServeSpec { Families = ShelteringFamilies, NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "sheltering" },
                Budget = TimeSpan.FromMinutes(12),
                Crew = new Crew { Size = 3 },
                Run = (s, ct) => RunAsync(s, c.Animal, ct),
            };
        }
    }

    private static async Task RunAsync(ISession s, bool withAnimal, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var expected = new List<string>();
        foreach (var raw in prepared["colonists"].AsSlice())
        {
            expected.Add(raw.AsString());
        }

        if (withAnimal)
        {
            expected.Add(prepared["animal"].AsString());
        }

        if (expected.Count == 0 || expected[0].Length == 0)
        {
            throw new AcceptanceException("prepare: no colonists to shelter: " + prepared.ToJson());
        }

        report["sheltered_pawns"] = new JsonArray([.. expected.Select(e => (JsonNode?)JsonValue.Create(e))]);

        // 1. The trigger holds: every expected pawn is moved into the Safe area.
        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        try
        {
            using var st = await ShelterStage.OpenJournalAsync(s, cancellationToken).ConfigureAwait(false);
            await WaitAreasAsync(st, expected, true, ShelterShell.WaitOf(ShelterWait, service), report, "shelter", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        report["shelter_service"] = await service.StopAsync().ConfigureAwait(false);
        var h = s.Reattach();

        // The journal settles when the area writes complete; the walk into the hut takes game time the stopped service no longer runs.
        JsonObject sheltered = [];
        _ = await RunAt.RunUntilAsync(
            h,
            "shelter-walk",
            ShelterWalkTicks,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var got = await StageAsync(h, "status", ct).ConfigureAwait(false);
                sheltered = got;
                return (got["pawns"].ToText(), CheckPawns(got, expected, true) is null);
            },
            cancellationToken).ConfigureAwait(false);
        report["sheltered"] = sheltered.DeepClone();
        if (CheckPawns(sheltered, expected, true) is { } failed)
        {
            throw new AcceptanceException(failed);
        }

        // 2. The trigger ends: the restarted service restores every pawn.
        var ended = await StageAsync(h, "end", cancellationToken).ConfigureAwait(false);
        report["ended"] = ended.DeepClone();
        if (ended["fallout"].AsBool() == true || ended["packOnMap"].AsNumber() != 0)
        {
            throw new AcceptanceException("the trigger did not end: " + ended.ToJson());
        }

        service = await ShelterShell.StartAsync(s, service, cancellationToken).ConfigureAwait(false);
        try
        {
            using var st = await ShelterStage.OpenJournalAsync(s, cancellationToken).ConfigureAwait(false);
            await WaitAreasAsync(st, expected, false, ShelterShell.WaitOf(ShelterWait, service), report, "restore", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        report["restore_service"] = await service.StopAsync().ConfigureAwait(false);
        h = s.Reattach();
        var restored = await StageAsync(h, "status", cancellationToken).ConfigureAwait(false);
        report["restored"] = restored.DeepClone();
        if (CheckPawns(restored, expected, false) is { } bad)
        {
            throw new AcceptanceException(bad);
        }
    }

    /// <summary>Calls <c>test/shelter_stage</c>.</summary>
    private static async Task<JsonObject> StageAsync(Harness h, string op, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("shelter-" + op, "test/shelter_stage", new JsonObject { ["op"] = op }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"shelter_stage {op} refused: {reply.ToJson()}");
        }

        return reply;
    }

    /// <summary>
    /// Asserts the native postcondition: sheltered, every expected pawn is restricted to the Safe area, stands in the hut and is
    /// unhurt; restored, none is restricted to it. Returns the failure text, or null when the status holds.
    /// </summary>
    private static string? CheckPawns(JsonObject status, IReadOnlyList<string> expected, bool sheltered)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var raw in status["pawns"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            rows[row["id"].AsString()] = row;
        }

        foreach (var id in expected)
        {
            if (!rows.TryGetValue(id, out var row))
            {
                return $"{id} is missing from the fixture status";
            }

            if (row["dead"].AsBool() == true)
            {
                return $"{id} died: {row.ToJson()}";
            }

            if (row["injuries"].AsNumber() != 0)
            {
                return $"{id} was harmed: {row.ToJson()}";
            }

            var area = row["area"].AsString();
            if (!sheltered)
            {
                if (area == PolicyFunctions.SafeAreaLabel)
                {
                    return $"{id} is still restricted to the Safe area: {row.ToJson()}";
                }

                continue;
            }

            if (area != PolicyFunctions.SafeAreaLabel)
            {
                return $"{id} is restricted to \"{area}\", not the Safe area: {row.ToJson()}";
            }

            if (row["inHut"].AsBool() != true)
            {
                return $"{id} is restricted to the Safe area but stands outside the hut: {row.ToJson()}";
            }
        }

        return null;
    }

    /// <summary>
    /// Polls the journal until every pawn's latest completed allowed-area action shelters it (a non-empty area) or, restoring,
    /// clears it. The plans are the sheltering planner's RecoverDisasterServices methods; restoring, a completed delete of the
    /// Safe area after the pawn's last move (MaintainShelter resets its areas in a new service) also unrestricts it. The native
    /// readback after the service stops is the assertion.
    /// </summary>
    private static async Task WaitAreasAsync(Journal st, IReadOnlyList<string> pawns, bool shelter, Wait w, Report report, string label, CancellationToken cancellationToken)
    {
        var last = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await w.WaitProgressAsync(
                async ct =>
                {
                    ReviewRounds review;
                    try
                    {
                        review = await st.LoadRoundsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-review", e.Message), false);
                    }

                    var world = new RimGovernor.Brain.Store.World(review.Snapshot.Colony, review.Snapshot.Load, review.Snapshot.Map);
                    var history = await st.IncidentHistoryAsync(world, PolicyFunctions.RecoverDisasterServices, ct).ConfigureAwait(false);
                    var areas = new Dictionary<string, string>(StringComparer.Ordinal);
                    var ticks = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var incident in history)
                    {
                        foreach (var m in incident.Methods)
                        {
                            var plan = await st.LoadPlanAsync(m.Plan, ct).ConfigureAwait(false);
                            for (var i = 0; i < plan.Spec.Actions.Count; i++)
                            {
                                var a = plan.Spec.Actions[i];
                                string pawn;
                                string area;
                                if (a.WorkAssignment is { HasArea: true } work)
                                {
                                    pawn = work.Pawn.Value;
                                    area = work.AreaClear ? string.Empty : work.Area;
                                }
                                else if (a.Husbandry is { Method: HusbandryMethod.AllowedArea } hb)
                                {
                                    pawn = hb.Animal.Value;
                                    area = hb.Argument;
                                }
                                else
                                {
                                    continue;
                                }

                                var v = plan.Progress[i].View;
                                var known = ticks.GetValueOrDefault(pawn);
                                if (v.Stage != Stage.Completed || (known != 0 && v.Tick.Value < known))
                                {
                                    continue;
                                }

                                areas[pawn] = area;
                                ticks[pawn] = v.Tick.Value;
                            }
                        }
                    }

                    long reset = 0;
                    var resetKnown = false;
                    var (goal, _) = await st.WorkableAsync(review, PolicyFunctions.MaintainShelter, ct).ConfigureAwait(false);
                    if (goal is not null)
                    {
                        foreach (var m in goal.History)
                        {
                            var plan = await st.LoadPlanAsync(m.Plan, ct).ConfigureAwait(false);
                            for (var i = 0; i < plan.Spec.Actions.Count; i++)
                            {
                                var v = plan.Progress[i].View;
                                if (plan.Spec.Actions[i].Area is { } edit
                                    && edit.Key == PolicyFunctions.SafeAreaKey
                                    && edit.Operation == AreaOperation.Delete
                                    && v.Stage == Stage.Completed
                                    && (!resetKnown || v.Tick.Value > reset))
                                {
                                    reset = v.Tick.Value;
                                    resetKnown = true;
                                }
                            }
                        }
                    }

                    last = new SortedDictionary<string, string>(areas, StringComparer.Ordinal);
                    if (!AreasSettled(areas, ticks, reset, resetKnown, pawns, shelter))
                    {
                        return (Wait.Signature(history.Count, AreasText(last), reset, resetKnown), false);
                    }

                    return ("", true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            report[label + "_areas"] = AreasJson(last);
            throw new AcceptanceException($"{label}: pawns' allowed areas did not settle (last {AreasText(last)}): {e.Message}", e);
        }

        report[label + "_areas"] = AreasJson(last);
    }

    private static string AreasText(SortedDictionary<string, string> areas) =>
        "map[" + string.Join(' ', areas.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key}:{e.Value}"))) + "]";

    private static JsonObject AreasJson(SortedDictionary<string, string> areas)
    {
        var json = new JsonObject();
        foreach (var (key, value) in areas)
        {
            json[key] = value;
        }

        return json;
    }

    /// <summary>
    /// Reports whether the journal shows every pawn sheltered (its latest completed move names an area) or restored: its latest
    /// move clears it, or the Safe area was deleted (<paramref name="resetKnown"/>, at <paramref name="reset"/>) after it. A pawn
    /// with no move at all is neither: an empty journal proves nothing.
    /// </summary>
    private static bool AreasSettled(Dictionary<string, string> areas, Dictionary<string, long> ticks, long reset, bool resetKnown, IReadOnlyList<string> pawns, bool shelter)
    {
        foreach (var p in pawns)
        {
            if (!areas.TryGetValue(p, out var area))
            {
                return false;
            }

            if (shelter && area.Length == 0)
            {
                return false;
            }

            if (!shelter && area.Length != 0 && !(resetKnown && reset > ticks.GetValueOrDefault(p)))
            {
                return false;
            }
        }

        return true;
    }
}
