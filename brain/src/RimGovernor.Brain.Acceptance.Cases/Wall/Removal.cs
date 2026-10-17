using System.Globalization;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/removal</c>: the wall_upgrade-guarded Designate over the staged upgrade room (<see cref="WallUpgrade"/>).
/// NativeWallRemovalOperations resolves the guarded demolition site from the wall at the named cell, refuses it before
/// any stone backup stands (and a different expected wall as stale), adopts the player's standing designation once
/// same-stuff granite backups are staged in its backup cells, returns its first result for a resent key and the same
/// removal for a new one, and a builder's deconstruct job clears the wall; the cleared cell applies again. A staged
/// granite permanent wall then makes the backups cleanup sites and one backup is demolished the same way, leaving two.
/// </summary>
internal static class WallRemoval
{
    private const string Stone = "BlocksGranite";

    internal static TypedCase Case() => new()
    {
        Name = "wall/removal",
        Scope = "Native wall_upgrade-guarded Designate over a staged roofed room: refused before backups (INVALID_REQUEST) and for a "
            + "different expected wall (STALE_IDENTITY); with staged granite backups it adopts a standing player designation with site "
            + "evidence, replays and reapplies on the same removal, a builder demolishes the wall and the cleared cell applies again; a "
            + "staged permanent granite wall turns the backups into cleanup sites and one is demolished, two remaining.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [WallKit.PlayerDesignateTool],
        Stage = WallUpgrade.Stage(),
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // A straight site with three open backup cells on one of the room's walls.
        (string Wall, Observations.WallUpgradeSite Row)? found = null;
        foreach (var wall in s.Staged!.Rooms["room"].Walls)
        {
            var row = (await WallUpgrade.CensusAsync(s, "candidates-" + wall, wall, cancellationToken).ConfigureAwait(false)).Sites
                .FirstOrDefault(r => !WallUpgrade.Corner(r) && string.IsNullOrEmpty(r.Blocker) && r.BackupCells.Count == 3 && r.CompletedBackups.Count == 0);
            if (row is not null)
            {
                found = (wall, row);
                break;
            }
        }

        var (target, site) = found ?? throw new AcceptanceException("no wall of the staged room offers a straight site with three open backup cells");
        var normal = site.Normal!;
        var cell = await CellAsync(s, "site-cell", target, cancellationToken).ConfigureAwait(false);
        s.Record("site_wall", target);

        Operations.DesignateIntent Guarded(Common.Cell at, string expected) => new()
        {
            Designation = Operations.ThingDesignation.Deconstruct,
            Cell = at,
            Guard = Operations.DesignationGuard.WallUpgrade,
            Target = new() { Id = expected },
        };

        async Task<Observations.WallUpgradeSite> RowAsync(string label, string? of)
        {
            var rows = (await WallUpgrade.CensusAsync(s, label, of, cancellationToken).ConfigureAwait(false)).Sites.Where(r => r.Normal == normal).ToList();
            return rows is [var row] ? row : throw new AcceptanceException($"{label}: want one row with normal {normal}, got {rows.Count}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var early = await TypedApply.RefusedAsync(s, "wall-early", Guarded(cell, target), cancellationToken).ConfigureAwait(false);
        var other = await TypedApply.RefusedAsync(s, "wall-other", Guarded(cell, "Thing_NoSuchWall0"), cancellationToken).ConfigureAwait(false);
        if (early.Code != Common.FailureCode.InvalidRequest || other.Code != Common.FailureCode.StaleIdentity)
        {
            throw new AcceptanceException($"refusals: want INVALID_REQUEST without backups and STALE_IDENTITY for another wall, got {early} and {other}");
        }

        // Completed backups: granite walls in the three backup cells make the site demolition ready.
        var backups = (await StageWallsAsync(s, "backups", site.BackupCells, cancellationToken).ConfigureAwait(false)).ToList();
        var ready = await RowAsync("ready-census", target).ConfigureAwait(false);
        if (ready.CompletedBackups.Count != 3 || !string.IsNullOrEmpty(ready.Blocker) || ready.Designated == true)
        {
            throw new AcceptanceException($"ready-census: want three completed backups, no blocker and no designation, got {ready}");
        }

        _ = await s.FixtureAsync("standing-designation", WallKit.PlayerDesignate, target, cancellationToken).ConfigureAwait(false);
        var standing = await RowAsync("standing-census", target).ConfigureAwait(false);
        if (standing.Designated != true || !string.IsNullOrEmpty(standing.RemovalId))
        {
            throw new AcceptanceException($"standing-census: want the player's designation and no removal, got {standing}");
        }

        var demolish = await ExecuteAsync(s, "wall-demolish", target, Guarded(cell, target), cancellationToken).ConfigureAwait(false);
        var removalId = demolish.Effect()!.Wall!.RemovalId;
        var designated = await RowAsync("designated-census", target).ConfigureAwait(false);
        if (designated.Designated != true || designated.RemovalId != removalId)
        {
            throw new AcceptanceException($"designated-census: want the ledger's removal {removalId}, got {designated}");
        }

        var replay = await TypedApply.AppliedAsync(s, "wall-demolish", Guarded(cell, target), cancellationToken).ConfigureAwait(false);
        if (replay != demolish)
        {
            throw new AcceptanceException($"replay: a resent key returned a different result: {demolish} then {replay}");
        }

        var again = await TypedApply.AppliedAsync(s, "wall-demolish-again", Guarded(cell, target), cancellationToken).ConfigureAwait(false);
        if (again.Effect()?.Wall?.RemovalId != removalId)
        {
            throw new AcceptanceException($"reapply: a new key must apply again on the pending removal {removalId}, got {again}");
        }

        await GoneAsync(s, "demolished", target, 10000, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "wall-demolish-cleared", Guarded(cell, target), cancellationToken).ConfigureAwait(false);

        // A standing permanent granite wall turns the backups into cleanup sites.
        var permanent = (await StageWallsAsync(s, "permanent", [cell], cancellationToken).ConfigureAwait(false)).Single();
        var cleanup = await RowAsync("cleanup-census", null).ConfigureAwait(false);
        var first = cleanup.Target?.Id ?? "";
        if (!backups.Contains(first) || cleanup.Replacement?.Id != permanent || cleanup.CompletedBackups.Count != 3)
        {
            throw new AcceptanceException($"cleanup-census: want a backup target and the permanent replacement {permanent}, got {cleanup}");
        }

        var backupCell = await CellAsync(s, "backup-cell", first, cancellationToken).ConfigureAwait(false);
        _ = await ExecuteAsync(s, "wall-cleanup", first, Guarded(backupCell, first), cancellationToken).ConfigureAwait(false);
        await GoneAsync(s, "cleaned", first, s.TicksLeft, cancellationToken).ConfigureAwait(false);
        var after = await RowAsync("after-cleanup-census", null).ConfigureAwait(false);
        var next = after.Target?.Id ?? "";
        if (after.CompletedBackups.Count != 2 || next == first || !backups.Contains(next))
        {
            throw new AcceptanceException($"after-cleanup-census: want two remaining backups naming the next, got {after}");
        }
    }

    /// <summary>Applies one guarded removal and checks its admission evidence: target, removal id, site tokens, nothing demolished yet.</summary>
    private static async Task<Operations.ActionResultOutcome> ExecuteAsync(TypedSession s, string key, string wall, Operations.DesignateIntent intent, CancellationToken cancellationToken)
    {
        var outcome = await TypedApply.AppliedAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        var effect = outcome.Effect()?.Wall;
        if (effect is null || effect.TargetId != wall || string.IsNullOrEmpty(effect.RemovalId) || effect.Site?.EntityId != wall
            || string.IsNullOrEmpty(effect.Site.BeforeToken) || string.IsNullOrEmpty(effect.Site.AfterToken) || effect.DemolitionObserved == true)
        {
            throw new AcceptanceException($"{key}: unexpected wall effect: {outcome}");
        }

        return outcome;
    }

    /// <summary>Runs until the building census no longer lists <paramref name="wall"/>.</summary>
    private static async Task GoneAsync(TypedSession s, string label, string wall, int ticks, CancellationToken cancellationToken)
    {
        var spent = await s.RunUntilAsync(label, ticks, async ct =>
        {
            var gone = !(await WallKit.PresentAsync(s, label + "-probe", [wall], ct).ConfigureAwait(false)).Contains(wall);
            return (Wait.Signature(gone), gone);
        }, cancellationToken).ConfigureAwait(false);
        s.Record(label + "_after_ticks", spent);
    }

    /// <summary>The cell a building stands on, from the building census.</summary>
    private static async Task<Common.Cell> CellAsync(TypedSession s, string label, string id, CancellationToken cancellationToken)
    {
        var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [id] }, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.Building.Id == id)?.Building.Position ?? throw new AcceptanceException($"{label}: {id} is not on the building census");
    }

    /// <summary>Stages finished player granite walls at absolute cells (clearing the leftovers there) and returns their ids in order.</summary>
    private static async Task<IReadOnlyList<string>> StageWallsAsync(TypedSession s, string name, IEnumerable<Common.Cell> cells, CancellationToken cancellationToken)
    {
        var at = cells.Select(c => new Fx.StageCell(c.X, c.Z)).ToList();
        var reply = await s.StageAsync(name, new Fx.StageSpec
        {
            Clear = [.. at.Select(c => new Fx.StageClear { Rect = new Fx.StageRect(c.X, c.Z, 1, 1), Kinds = [Fx.StageClearKind.Things] })],
            Buildings = [.. at.Select((c, i) => new Fx.StageBuilding { Name = name + "-" + i.ToString(CultureInfo.InvariantCulture), Def = "Wall", Stuff = Stone, Cells = [c] })],
        }, cancellationToken).ConfigureAwait(false);
        return [.. at.Select((_, i) => reply.Buildings[name + "-" + i.ToString(CultureInfo.InvariantCulture)].Ids.Single())];
    }
}
