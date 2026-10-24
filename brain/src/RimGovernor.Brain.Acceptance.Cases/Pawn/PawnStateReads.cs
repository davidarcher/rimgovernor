using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>pawn/inspiration</c> and <c>pawn/mental-state</c>: a colonist staged into a native inspiration or mental state
/// reads it back through observations_list_pawns. One typed case per row.
/// </summary>
internal static class PawnStateReads
{
    private sealed record Row(string Name, string Scope, Fx.StagePawn Pawn, Func<Observations.PawnState, bool> Read);

    private static readonly Row[] Rows =
    [
        new(
            "pawn/inspiration",
            "A colonist staged with Inspired_Creativity reads that InspirationDef through the pawn read (#1187).",
            new Fx.StagePawn { Name = "pawn", Colonist = 0, Inspiration = "Inspired_Creativity" },
            p => p.Inspiration == "Inspired_Creativity"),
        new(
            "pawn/mental-state",
            "A colonist forced into Berserk reads its native MentalStateDef, aggression and zero age in the state through the pawn read.",
            new Fx.StagePawn { Name = "pawn", Colonist = 0, MentalState = "Berserk" },
            p => p.MentalState is { DefName: "Berserk", IsAggro: true, Ticks: 0 }),
    ];

    internal static IEnumerable<TypedCase> All() => Rows.Select(row => new TypedCase
    {
        Name = row.Name,
        Scope = row.Scope,
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec { Paused = true, Pawns = [row.Pawn] },
        QuietWorld = true,
        Run = (s, ct) => RunAsync(s, row, ct),
    });

    private static async Task RunAsync(TypedSession s, Row row, CancellationToken cancellationToken)
    {
        var id = s.Staged?.Pawns["pawn"].Id ?? throw new AcceptanceException("the stage made no pawn");
        var pawn = await TypedPawns.OneAsync(s, "pawn", id, cancellationToken).ConfigureAwait(false);
        s.RecordWire("mental_state", pawn.MentalState);
        s.Record("inspiration", pawn.Inspiration);
        if (!row.Read(pawn))
        {
            throw new AcceptanceException($"{id} reads inspiration \"{pawn.Inspiration}\", mental state {pawn.MentalState}");
        }
    }
}
