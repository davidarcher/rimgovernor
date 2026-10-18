using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/recreation-gain</c> (#3570): a colony whose only recreation is a Game-of-Ur board (no horseshoes pin) has a known joy
/// gain. The needs forecast lists the board as the colonist's joy target with a positive gain per tick and the giver job's
/// joyMaxParticipants as its capacity, both read from the game's JoyGiverDef rather than a hard-coded building.
/// </summary>
internal static class NeedsRecreationGain
{
    private const string Board = "board";

    internal static TypedCase Case() => new()
    {
        Name = "needs/recreation-gain",
        Scope = "A paused colony whose only recreation is a Game-of-Ur board has the needs forecast list that board as the colonist's "
            + "joy target with a known positive gain per tick and a known capacity. A unit test cannot prove the native JoyGiverDef "
            + "lookup, the giver's own legality or the game's JoyGainFactor and tolerance reads.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 5,
                Height = 5,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 5), Kinds = [Fx.StageClearKind.Things] }],
            Buildings = [new Fx.StageBuilding { Name = Board, Def = "GameOfUrBoard", Stuff = "WoodLog", Cells = [new(2, 2)] }],
            Pawns = [new Fx.StagePawn { Name = "colonist", Colonist = 0 }],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var colonist = s.Staged?.Pawns["colonist"].Id ?? throw new AcceptanceException("the stage made no colonist");
        var board = s.Staged!.Buildings[Board].Ids.Single();
        var reply = await s.CallAsync("needs", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var needs = TypedReads.Observed("needs", NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed).Forecast?.Outcome?.Needs
            ?? throw new AcceptanceException("needs: needs forecast unavailable");
        var row = needs.Pawns.SingleOrDefault(p => p.Pawn == colonist) ?? throw new AcceptanceException($"needs: no row for {colonist}");
        s.RecordWire("joy_targets", row.Targets.Where(t => t.Kind == "joy").ToArray());
        if (row.State != "eligible")
        {
            throw new AcceptanceException($"the colonist is not eligible, so no targets are read: {row.State} [{string.Join(' ', row.VoluntaryControlBlockers)}]");
        }

        var target = row.Targets.SingleOrDefault(t => t.Id == board)
            ?? throw new AcceptanceException($"the board {board} is not a target: [{string.Join(' ', row.Targets.Select(t => $"{t.Kind}:{t.Id}"))}]");
        if (target.Kind != "joy" || !target.GainPerTick.TryGetValue(out var gain) || !(gain > 0) || !target.MaxPawns.TryGetValue(out var capacity) || capacity < 1)
        {
            throw new AcceptanceException($"the board is not a joy target with a known gain and capacity: {target}");
        }
    }
}
