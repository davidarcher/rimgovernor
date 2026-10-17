using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the quest cases share: the quest fixtures' typed ops and one quest's world-progression row.</summary>
internal static class QuestKit
{
    internal const string AcceptTool = "test/quest_accept_prepare";
    internal const string HackPrepareTool = "test/quest_hack_prepare";
    internal const string HackReadTool = "test/quest_hack_read";
    internal const string GivePrepareTool = "test/quest_give_prepare";
    internal const string GiveReadTool = "test/quest_give_read";
    internal const string ShuttleTool = "test/quest_shuttle_prepare";

    internal static readonly FixtureOp<NoArgs, Fx.QuestAcceptPrepared> Accept = new(AcceptTool, _ => [], ServedOps.Succeeded<Fx.QuestAcceptPrepared>(AcceptTool, r => r.Success));

    internal static readonly FixtureOp<NoArgs, Fx.QuestHackPrepared> HackPrepare = new(HackPrepareTool, _ => [], ServedOps.Succeeded<Fx.QuestHackPrepared>(HackPrepareTool, r => r.Success));

    internal static readonly FixtureOp<string, Fx.QuestHackRead> HackRead = new(
        HackReadTool,
        terminal => ServedOps.Args(("terminalId", terminal)),
        ServedOps.Succeeded<Fx.QuestHackRead>(HackReadTool, r => r.Success));

    internal static readonly FixtureOp<NoArgs, Fx.QuestGivePrepared> GivePrepare = new(GivePrepareTool, _ => [], ServedOps.Succeeded<Fx.QuestGivePrepared>(GivePrepareTool, r => r.Success));

    internal static readonly FixtureOp<Fx.QuestGivePrepared, Fx.QuestGiveRead> GiveRead = new(
        GiveReadTool,
        p => ServedOps.Args(("recipientId", p.RecipientId), ("haulerId", p.HaulerId)),
        ServedOps.Succeeded<Fx.QuestGiveRead>(GiveReadTool, r => r.Success));

    internal static readonly FixtureOp<NoArgs, Fx.QuestShuttlePrepared> Shuttle = new(ShuttleTool, _ => [], ServedOps.Succeeded<Fx.QuestShuttlePrepared>(ShuttleTool, r => r.Success));

    /// <summary>The world-progression row of <paramref name="questId"/>, null when native lists no such quest.</summary>
    internal static async Task<Observations.QuestState?> QuestAsync(TypedSession s, string label, string questId, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadWorldProgression, new Observations.WorldProgressionRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        return TypedReads.Observed(label, NativeOps.ReadWorldProgression, reply, reply.Outcome?.Observed).Quests.LastOrDefault(q => q.Id == questId);
    }
}
