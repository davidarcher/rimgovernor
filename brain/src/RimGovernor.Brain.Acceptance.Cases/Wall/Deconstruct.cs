using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/deconstruct</c>: the enclosure-guarded DECONSTRUCT Designate over six staged lone WoodLog walls (two the
/// colony's, four unowned). It adopts a player's own designation with exact evidence, designates unowned walls and
/// applies again on owned work; a manual revoke releases only the supervisor's own work (an adopted order, an owned one)
/// and keeps a player's (a foreign order, one the player re-laid), coping with a wall that vanished; a resent key returns
/// its first result and a builder demolishes the adopted wall. Native: the designation manager, the release ledger and
/// the vanilla deconstruct job.
/// </summary>
internal static class WallDeconstruct
{
    private const int Width = 11;
    private const int Height = 6;

    private static readonly Fx.StageCell[] Walls = [new(1, 1), new(4, 1), new(7, 1), new(1, 4), new(4, 4), new(7, 4)];

    internal static TypedCase Case() => new()
    {
        Name = "wall/deconstruct",
        Scope = "DECONSTRUCT Designate on six staged lone walls: adopts a player designation with its designation id, designates "
            + "unowned walls and applies again on owned work; a manual revoke releases the adopted and owned orders and keeps a foreign "
            + "and a player re-laid one, with one target vanished; a resent key returns its first result and a builder demolishes an "
            + "adopted wall.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [WallKit.PlayerDesignateTool],
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = WallKit.Site(Width, Height),
            Clear = [WallKit.Clear(Width, Height)],
            Buildings =
            [
                .. Walls.Select((c, i) => new Fx.StageBuilding
                {
                    Name = Name(i),
                    Def = "Wall",
                    Stuff = "WoodLog",
                    Cells = [c],
                    Faction = i is 0 or 5 ? Fx.StageFaction.Player : Fx.StageFaction.None,
                }),
            ],
            Pawns = WallKit.Builders,
        },
        Run = RunAsync,
    };

    private static string Name(int i) => "wall-" + i.ToString(CultureInfo.InvariantCulture);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var ids = Enumerable.Range(0, Walls.Length).Select(i => staged.Buildings[Name(i)].Ids.Single()).ToList();
        var cells = Enumerable.Range(0, Walls.Length).Select(i => WallKit.At(site, Walls[i])).ToList();

        // Player orders: wall 0 (the colony's, to be adopted) and wall 1 (unowned, never touched by the supervisor).
        _ = await s.FixtureAsync("player-order-0", WallKit.PlayerDesignate, ids[0], cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("player-order-1", WallKit.PlayerDesignate, ids[1], cancellationToken).ConfigureAwait(false);

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var adopted = await TypedApply.AppliedAsync(s, "adopt-0", WallKit.Deconstruct(ids[0]), cancellationToken).ConfigureAwait(false);
        var evidence = adopted.Effect()?.Deconstruct;
        if (evidence is null || evidence.TargetId != ids[0] || string.IsNullOrEmpty(evidence.DesignationId))
        {
            throw new AcceptanceException($"adopt-0: want deconstruct evidence naming {ids[0]} and its designation, got {adopted}");
        }

        foreach (var i in new[] { 2, 3, 4 })
        {
            _ = await TypedApply.AppliedAsync(s, "designate-" + i.ToString(CultureInfo.InvariantCulture), WallKit.Deconstruct(ids[i]), cancellationToken).ConfigureAwait(false);
        }

        // The player re-lays wall 2's order (it becomes theirs) and wall 4 vanishes under the supervisor.
        _ = await s.FixtureAsync("player-relay-2", WallKit.PlayerDesignate, ids[2], cancellationToken).ConfigureAwait(false);
        _ = await s.StageAsync("vanish-4", new Fx.StageSpec
        {
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(cells[4].X, cells[4].Z, 1, 1), Kinds = [Fx.StageClearKind.Things] }],
        }, cancellationToken).ConfigureAwait(false);

        _ = await TypedApply.AppliedAsync(s, "designate-3-again", WallKit.Deconstruct(ids[3]), cancellationToken).ConfigureAwait(false);
        await TypedAuthority.RevokeManualAsync(s, cancellationToken).ConfigureAwait(false);

        var present = await WallKit.PresentAsync(s, "after-release", ids, cancellationToken).ConfigureAwait(false);
        var grid = await TypedApply.CellsAsync(s, "after-release", site, cancellationToken).ConfigureAwait(false);
        foreach (var i in new[] { 0, 1, 2, 3 })
        {
            var designated = grid.ThingsAt(cells[i]).Any(t => t.Def == "Wall" && t.Has(ThingMarks.Designated));
            var want = i is 1 or 2;
            if (!present.Contains(ids[i]) || designated != want)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"after-release: wall {i} present={present.Contains(ids[i])} designated={designated}, want present and designated={want}"));
            }
        }

        // A colony wall the player ordered down: adopted, resent under the same key, then demolished by a builder.
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("player-order-5", WallKit.PlayerDesignate, ids[5], cancellationToken).ConfigureAwait(false);
        var first = await TypedApply.AppliedAsync(s, "adopt-and-finish", WallKit.Deconstruct(ids[5]), cancellationToken).ConfigureAwait(false);
        var again = await TypedApply.AppliedAsync(s, "adopt-and-finish", WallKit.Deconstruct(ids[5]), cancellationToken).ConfigureAwait(false);
        if (first != again)
        {
            throw new AcceptanceException($"adopt-and-finish: a resent key changed its result: {first} then {again}");
        }

        // The adopted record is the supervisor's, so a builder takes it only while the supervisor runs the clock.
        await WallKit.DemolishedAsync(s, "demolished", ids[5], s.TicksLeft, cancellationToken).ConfigureAwait(false);
    }
}
