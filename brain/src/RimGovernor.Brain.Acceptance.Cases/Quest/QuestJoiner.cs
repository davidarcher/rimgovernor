using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the durable journal proves for a joiner, favor or quest offer: the joiners family Method whose plan carries a
/// QuestAccept for it (Go <c>joinerAnswer</c>).
/// </summary>
internal sealed record JoinerAnswer(string Method, string Plan, int Reward, string Stage)
{
    internal JsonObject ToJson() => new()
    {
        ["method"] = Method,
        ["plan"] = Plan,
        ["reward_choice"] = Reward,
        ["stage"] = Stage,
    };
}

/// <summary>
/// <c>quest/joiner</c> (Go <c>joiner.go</c>): a real ThreatReward_Raid_Joiner offer, generated through the native
/// storyteller path, reads back in the quest census with its root script_def; with capacity (spare unowned beds and a
/// food runway past the joiner floor, both staged by the fixture) the population planner accepts it through the
/// QuestAccept vertical, the joiner walks into the colony and the native quest reports itself accepted and ongoing.
/// </summary>
internal static class QuestJoiner
{
    private const string PrepareTool = "test/joiner_quest_prepare";
    private const string ReadTool = "test/joiner_quest_read";

    /// <summary>Bounds each service-side wait; the stall budget ends it earlier when nothing moves.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Bounds the post-acceptance run for the joiner: ThreatReward_Raid_Joiner walks them in 600..1200 ticks after the
    /// accept, well before its raid (a further 1800..2400 ticks).
    /// </summary>
    private const ulong ArrivalTicks = 3000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/joiner",
        Scope = "Issue #250: a real ThreatReward_Raid_Joiner offer reads back in the quest census with its root " +
            "script_def and, with capacity under the bot's population target (#1032: food floor, a spare unowned bed), " +
            "the joiners family accepts it through the " +
            "QuestAccept vertical and the joiner arrives natively.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },

        // The building families keep supervised windows running; the joiner planner alone never advances the clock.
        Serve = new ServeSpec
        {
            Families = ["population-joiner", "supply", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "quest-joiner",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// Every QuestAccept plan the joiners family has admitted (its <c>joiner-accept-*</c> and <c>quest-accept-*</c>
    /// Methods, #3302), keyed by the accepted quest; Found says whether any plan was admitted.
    /// </summary>
    internal static async Task<(Dictionary<string, JoinerAnswer> Answers, bool Found)> AnswersAsync(ICaseJournal journal, CancellationToken cancellationToken)
    {
        var plans = await journal.PlanHistoryAsync(256, ["joiner-accept-*", "quest-accept-*"], cancellationToken).ConfigureAwait(false);
        var answers = new Dictionary<string, JoinerAnswer>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            foreach (var step in plan["steps"].AsSlice())
            {
                var row = step.AsMap() ?? [];
                if (row["kind"].AsString() != Wire.Name(ActionKind.QuestAccept))
                {
                    continue;
                }

                var accept = row["action"].AsMap() ?? [];
                answers[accept["quest"].AsString()] = new JoinerAnswer(
                    plan["method"].AsString(),
                    plan["plan"].AsString(),
                    (int)Num(accept["rewardChoice"]),
                    row["stage"].AsString());
            }
        }

        return (answers, plans.Count > 0);
    }

    internal static JsonObject AnswersJson(Dictionary<string, JoinerAnswer> answers)
    {
        var json = new JsonObject();
        foreach (var (quest, answer) in answers)
        {
            json[quest] = answer.ToJson();
        }

        return json;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!s.Names.Contains(ReadTool))
        {
            throw new AcceptanceException($"missing {ReadTool} in discovery; rebuild the native mod with -Fixture QuestAcceptFixture");
        }

        var questId = prepared["questId"].AsString();
        if (questId.Length == 0 || prepared["scriptDef"].AsString() != "ThreatReward_Raid_Joiner" || prepared["state"].AsString() != "NotYetAccepted")
        {
            throw new AcceptanceException($"fixture: unexpected joiner offer: {prepared.ToJson()}");
        }

        if (prepared["beds"].AsSlice().Count == 0)
        {
            throw new AcceptanceException($"fixture: no spare bed spawned: {prepared.ToJson()}");
        }

        var before = prepared["colonists"].AsSlice().Count;
        if (before == 0)
        {
            throw new AcceptanceException($"fixture: no colonists: {prepared.ToJson()}");
        }

        report["fixture_quest"] = questId;
        report["colonists_before"] = before;

        // The census the service's own review will read must already carry the offer with its root script name,
        // exactly as the quest offer decodes it (observations_read_world_progression's quests page).
        var observed = await TradeKit.ObserveAsync(h, "census-before", "observations_read_world_progression", identity, new JsonObject { ["includeStorage"] = false }, cancellationToken).ConfigureAwait(false);
        JsonObject? row = null;
        foreach (var raw in observed["quests"].AsSlice())
        {
            var candidate = raw.AsMap() ?? [];
            if (candidate["id"].AsString() == questId)
            {
                row = candidate;
            }
        }

        if (row is null)
        {
            throw new AcceptanceException($"census-before: the joiner offer is missing from the quest census: {observed["quests"].ToJson()}");
        }

        report["census_before"] = row.DeepClone();
        if (row["scriptDef"].AsString() != "ThreatReward_Raid_Joiner")
        {
            throw new AcceptanceException($"census-before: quest row lacks its root script_def: {row.ToJson()}");
        }

        if (row["state"].AsString() is not ("QUEST_STATUS_NOT_YET_ACCEPTED" or "NotYetAccepted"))
        {
            throw new AcceptanceException($"census-before: expected a not-yet-accepted offer: {row.ToJson()}");
        }

        if (!Flag(row["canAccept"]))
        {
            throw new AcceptanceException($"census-before: the offer is not acceptable: {row.ToJson()}");
        }

        if (Flag(row["requiresAccepter"]))
        {
            throw new AcceptanceException($"census-before: a joiner offer should not name an accepter: {row.ToJson()}");
        }

        var service = await QuestLifecycle.StartServiceAsync(s, cancellationToken).ConfigureAwait(false);
        Dictionary<string, JoinerAnswer> answers = [];
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            // joiners/offers admits one QuestAccept for the offer against the bot's own population target and the
            // executor completes it against the live quest.
            var wait = new Wait { Ceiling = Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited };
            try
            {
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        var (current, found) = await AnswersAsync(journal, ct).ConfigureAwait(false);
                        answers = current;
                        foreach (var (quest, answer) in current)
                        {
                            if (quest != questId)
                            {
                                throw new AcceptanceException($"the joiners family accepted quest {quest}, not the joiner offer {questId}");
                            }

                            if (answer.Stage is "unsuccessful" or "cancelled")
                            {
                                throw new AcceptanceException($"the joiner acceptance ended {answer.Stage}");
                            }
                        }

                        var done = current.TryGetValue(questId, out var mine) && mine.Stage == "completed";
                        return (Wait.Signature(found, AnswersJson(current).ToJsonString()), done);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                report["phase2_answers"] = AnswersJson(answers);
                throw new AcceptanceException("phase2: " + e.Message, e);
            }

            report["phase2_answers"] = AnswersJson(answers);
        }

        if (answers[questId].Reward != -1)
        {
            // ThreatReward roots carry a reward-choice part, whose first option the planner takes; -1 means the census
            // reported no choices.
            report["reward_choice"] = answers[questId].Reward;
        }

        report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();

        // Independent native read after the service releases the game slot: the quest is accepted and ongoing, and
        // running the game on lets the quest's own parts walk the joiner in as a free colonist.
        h = s.Reattach();
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var after = await h.CallAsync("quest-after", ReadTool, new JsonObject { ["questId"] = questId }, cancellationToken).ConfigureAwait(false);
        report["quest_after"] = after.DeepClone();
        if (!Flag(after["questFound"]))
        {
            throw new AcceptanceException($"quest-after: the joiner quest is gone: {after.ToJson()}");
        }

        if (after["state"].AsString() == "NotYetAccepted")
        {
            throw new AcceptanceException($"quest-after: the joiner quest was never accepted natively: {after.ToJson()}");
        }

        if ((long)Num(after["acceptedTick"]) < 0)
        {
            throw new AcceptanceException($"quest-after: no native acceptance tick: {after.ToJson()}");
        }

        JsonObject? arrived = null;
        var elapsed = await RunAt.RunUntilAsync(
            h,
            "joiner-arrival",
            ArrivalTicks,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var read = await h.CallAsync("joiner-read", ReadTool, new JsonObject { ["questId"] = questId }, ct).ConfigureAwait(false);
                arrived = read;
                var state = read["state"].AsString();
                if (state is "EndedFailed" or "EndedInvalid")
                {
                    throw new AcceptanceException($"joiner-arrival: the quest ended {state}: {read.ToJson()}");
                }

                var count = read["colonists"].AsSlice().Count;
                return (Wait.Signature(count, state), count > before);
            },
            quiet: true,
            report: report,
            cancellationToken).ConfigureAwait(false);
        report["joiner_arrival"] = arrived?.DeepClone();
        report["joiner_arrival_ticks"] = elapsed;
        report["colonists_after"] = arrived?["colonists"].AsSlice().Count ?? 0;
    }
}
