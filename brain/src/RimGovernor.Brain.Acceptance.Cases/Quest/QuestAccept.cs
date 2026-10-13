using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/accept</c> (Go <c>accept.go</c>): the AcceptQuestIntent arm of Actions/Apply end to end against a live game:
/// a real not-yet-accepted quest carrying a two-option native reward-choice part, native acceptance (an actual
/// Quest.Accept settings write and its QuestPart_Choice.Choose call) through the same <c>operations_apply</c> wire
/// contract, and its replay semantics. Uses a private disposable fixture (<c>test/quest_accept_prepare</c>) since a
/// minimal quest with an exact reward-choice shape cannot be produced deterministically through native random quest
/// generation. The fixture quest carries no native QuestPart_RequirementsToAccept part, so this case exercises the
/// "this quest does not accept an accepter" refusal branch by supplying one anyway.
/// </summary>
internal static class QuestAccept
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/accept",
        Scope = "Native AcceptQuestIntent: an actual Quest.Accept settings write and "
            + "QuestPart_Choice reward selection, out-of-range reward and accepter-not-required "
            + "refusals, replay idempotency and an already-accepted quest applied again.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_accept_prepare" },
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static bool NotYetAccepted(JsonObject row) => row["state"].AsString() is "QUEST_STATUS_NOT_YET_ACCEPTED" or "NotYetAccepted";

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_read_world_progression"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_read_world_progression in discovery");
        }

        var questId = prepared["questId"].AsString();
        var pawnIds = Texts(prepared["pawnIds"]);
        if (questId.Length == 0 || pawnIds.Count < 1)
        {
            throw new AcceptanceException($"quest_accept_prepare: unexpected fixture identifiers: {prepared.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // Target reads the world-progression census for the fixture's exact quest, the census the joiner planner
        // chooses offers from.
        async Task<JsonObject> Target(string label)
        {
            var observed = await TradeKit.ObserveAsync(
                h, label, "observations_read_world_progression", identity, new JsonObject { ["includeStorage"] = false }, cancellationToken).ConfigureAwait(false);
            return Rows(observed["quests"]).LastOrDefault(row => row["id"].AsString() == questId)
                ?? throw new AcceptanceException($"{label}: fixture quest not found in world progression: {observed.ToJson()}");
        }

        var questRow = await Target("target-before").ConfigureAwait(false);
        if (!NotYetAccepted(questRow))
        {
            throw new AcceptanceException($"target-before: expected a not-yet-accepted quest, got {questRow.ToJson()}");
        }

        if (Flag(questRow["requiresAccepter"]))
        {
            throw new AcceptanceException($"target-before: expected the fixture quest to not require an accepter, got {questRow.ToJson()}");
        }

        if (!Flag(questRow["canAccept"]))
        {
            throw new AcceptanceException($"target-before: expected the fixture quest to be acceptable, got {questRow.ToJson()}");
        }

        async Task<JsonObject> Apply(string label, string key, string accepterPawn, int rewardChoice)
        {
            var intent = new JsonObject { ["questId"] = questId, ["rewardChoice"] = rewardChoice };
            if (accepterPawn.Length > 0)
            {
                intent["accepterPawnId"] = accepterPawn;
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["acceptQuest"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            return OneResult(label, reply);
        }

        async Task Refused(string label, string key, string accepterPawn, int rewardChoice, string code)
        {
            var result = await Apply(label, key, accepterPawn, rewardChoice).ConfigureAwait(false);
            var refusal = result["refused"].AsMap();
            if (refusal is null || refusal["code"].AsString() != code || refusal["reason"].AsString().Length == 0)
            {
                throw new AcceptanceException($"{label}: expected a {code} refusal with a reason, got {result.ToJson()}");
            }
        }

        void AcceptedQuest(string label, JsonObject result)
        {
            var quest = result["applied"].AsMap()?["applied"].AsMap()?["observed"].AsMap()?["quest"].AsMap();
            if (quest?["questId"].AsString() != questId)
            {
                throw new AcceptanceException($"{label}: expected an applied receipt for the quest, got {result.ToJson()}");
            }

            if (!Flag(quest["accepted"]))
            {
                throw new AcceptanceException($"{label}: expected the quest to be accepted, got {quest.ToJson()}");
            }
        }

        // Refusal 1: a reward option outside the quest's single choice part.
        await Refused("reward-out-of-range", "quest-accept-reward", "", 5, "FAILURE_CODE_INVALID_REQUEST").ConfigureAwait(false);

        // Refusal 2: this quest does not require an accepter; supplying one anyway is refused rather than silently ignored.
        await Refused("unwanted-accepter", "quest-accept-unwanted-accepter", pawnIds[0], 0, "FAILURE_CODE_INVALID_REQUEST").ConfigureAwait(false);
        var afterRefusals = await Target("target-after-refusals").ConfigureAwait(false);
        if (!NotYetAccepted(afterRefusals))
        {
            throw new AcceptanceException($"target-after-refusals: a refused intent changed the quest: {afterRefusals.ToJson()}");
        }

        // Apply: the real native Quest.Accept settings write and its QuestPart_Choice.Choose call.
        var result = await Apply("apply", "quest-accept", "", 0).ConfigureAwait(false);
        AcceptedQuest("apply", result);
        var questRowAccepted = await Target("target-after-apply").ConfigureAwait(false);
        if (NotYetAccepted(questRowAccepted))
        {
            throw new AcceptanceException($"target-after-apply: expected the quest to have left NotYetAccepted, got {questRowAccepted.ToJson()}");
        }

        if (Flag(questRowAccepted["canAccept"]))
        {
            throw new AcceptanceException($"target-after-apply: expected canAccept=false for an already-accepted quest, got {questRowAccepted.ToJson()}");
        }

        // Replay: the same key returns the identical result without a second native write.
        var replay = await Apply("replay", "quest-accept", "", 0).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, result))
        {
            throw new AcceptanceException($"replay: the same key returned a different result: {replay.ToJson()}");
        }

        // A fresh key against the accepted quest is applied again: the intent already holds, so a resend after a lost
        // reply cannot fail it.
        var again = await Apply("apply-again", "quest-accept-again", "", 0).ConfigureAwait(false);
        AcceptedQuest("apply-again", again);
    }
}
