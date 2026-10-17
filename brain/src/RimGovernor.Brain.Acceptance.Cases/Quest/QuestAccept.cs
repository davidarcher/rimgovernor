using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/accept</c>: the AcceptQuestIntent arm of operations_apply against a real not-yet-accepted quest carrying a
/// two-option native reward-choice part (<c>test/quest_accept_prepare</c>; native random generation cannot pin that
/// shape). An out-of-range reward and an accepter the quest does not take are refused without touching it; the accept
/// writes Quest.Accept and QuestPart_Choice.Choose, its key replays, and a fresh key against the accepted quest applies
/// again.
/// </summary>
internal static class QuestAccept
{
    internal static TypedCase Case() => new()
    {
        Name = "quest/accept",
        Scope = "Native AcceptQuestIntent: an actual Quest.Accept settings write and "
            + "QuestPart_Choice reward selection, out-of-range reward and accepter-not-required "
            + "refusals, replay idempotency and an already-accepted quest applied again.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [QuestKit.AcceptTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", QuestKit.Accept, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var questId = prepared.QuestId;
        if (questId.Length == 0 || prepared.PawnIds.Count < 1)
        {
            throw new AcceptanceException($"prepare: unexpected fixture identifiers: {ServedOps.Json(prepared)}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Observations.QuestState> Target(string label) =>
            await QuestKit.QuestAsync(s, label, questId, cancellationToken).ConfigureAwait(false)
                ?? throw new AcceptanceException($"{label}: fixture quest {questId} not found in world progression");

        var before = await Target("target-before").ConfigureAwait(false);
        if (before.State != Receipts.QuestStatus.NotYetAccepted || before.RequiresAccepter == true || before.CanAccept != true)
        {
            throw new AcceptanceException($"target-before: expected an acceptable not-yet-accepted quest needing no accepter, got {before}");
        }

        Task<Operations.ActionResult> Apply(string key, string? accepter, int rewardChoice) => TradeKit.ApplyAsync(s, key, new Operations.AcceptQuestIntent
        {
            QuestId = questId,
            AccepterPawnId = accepter,
            RewardChoice = rewardChoice,
        }, cancellationToken);

        async Task Refused(string key, string? accepter, int rewardChoice)
        {
            var result = await Apply(key, accepter, rewardChoice).ConfigureAwait(false);
            if (result.Outcome?.Refused is not { Code: Common.FailureCode.InvalidRequest, Reason.Length: > 0 })
            {
                throw new AcceptanceException($"{key}: expected an INVALID_REQUEST refusal with a reason, got {result}");
            }
        }

        void Accepted(string label, Operations.ActionResult result)
        {
            if (TradeKit.Applied(label, result).Quest is not { Accepted: true } quest || quest.QuestId != questId)
            {
                throw new AcceptanceException($"{label}: expected an applied receipt accepting {questId}, got {result}");
            }
        }

        // A reward option outside the quest's single choice part, and an accepter the quest does not take.
        await Refused("quest-accept-reward", null, 5).ConfigureAwait(false);
        await Refused("quest-accept-unwanted-accepter", prepared.PawnIds[0], 0).ConfigureAwait(false);
        var afterRefusals = await Target("target-after-refusals").ConfigureAwait(false);
        if (afterRefusals.State != Receipts.QuestStatus.NotYetAccepted)
        {
            throw new AcceptanceException($"target-after-refusals: a refused intent changed the quest: {afterRefusals}");
        }

        var result = await Apply("quest-accept", null, 0).ConfigureAwait(false);
        Accepted("apply", result);
        var after = await Target("target-after-apply").ConfigureAwait(false);
        s.RecordWire("accepted", after);
        if (after.State == Receipts.QuestStatus.NotYetAccepted || after.CanAccept == true || after.AcceptedTick is not >= 0)
        {
            throw new AcceptanceException($"target-after-apply: expected an accepted quest with its acceptance tick, no longer acceptable, got {after}");
        }

        // The same key returns the identical result without a second native write.
        var replay = await Apply("quest-accept", null, 0).ConfigureAwait(false);
        if (replay != result)
        {
            throw new AcceptanceException($"replay: the same key returned a different result: {replay}");
        }

        // A fresh key against the accepted quest is applied again: a resend after a lost reply cannot fail it.
        Accepted("apply-again", await Apply("quest-accept-again", null, 0).ConfigureAwait(false));
    }
}
