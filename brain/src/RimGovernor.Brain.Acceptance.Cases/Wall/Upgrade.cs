using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/upgrade</c>: the typed wall-upgrade census (observations_list_wall_upgrade_sites) over a staged roofed
/// WoodLog room. The cleanup census (no target) lists only complete cleanup rows; per room wall, every replacement row
/// targets the original wall with its CAS token, a normal, both side supports, and three backup cells for a straight
/// site or none for a corner; at least one straight site is found. A blank target is refused as invalid, an unknown one
/// as not found, and the pass spends no ticks. Native: vanilla room, roof and wall geometry.
/// </summary>
internal static class WallUpgrade
{
    internal const int Width = 11;
    internal const int Height = 11;

    /// <summary>The room's rectangle: a 3x3 roofed interior with its door mid-south, so the north, east and west middles are straight sites.</summary>
    internal static readonly Fx.StageRect Room = new(3, 3, 5, 5);

    internal static TypedCase Case() => new()
    {
        Name = "wall/upgrade",
        Scope = "Typed wall-upgrade census over a staged roofed WoodLog room: complete cleanup rows; per room wall, replacement rows "
            + "naming the original with a CAS token, a normal, both supports and 3 backup cells straight or 0 at a corner, at least one "
            + "straight; blank and unknown targets refused (INVALID_REQUEST, NOT_FOUND); read-only, no ticks spent.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = Stage(),
        Run = RunAsync,
    };

    /// <summary>The upgrade room on open heavy ground with room for backups outside it, and the builder crew.</summary>
    internal static Fx.StageSpec Stage() => new()
    {
        Paused = true,
        Site = WallKit.Site(Width, Height),
        Clear = [WallKit.Clear(Width, Height)],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, Width, Height), Home = true }],
        Rooms = [new Fx.StageRoom { Name = "room", Rect = Room, Stuff = "WoodLog", Doors = [new(5, 3)], Roof = true }],
        Pawns = WallKit.Builders,
    };

    /// <summary>The census rows for <paramref name="target"/> (null: the cleanup census).</summary>
    internal static async Task<Observations.WallUpgradeSnapshot> CensusAsync(ITypedSession s, string label, string? target, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListWallUpgradeSites, new Observations.WallUpgradeSitesRequest { Scope = s.Scope, TargetId = target }, cancellationToken).ConfigureAwait(false);
        var snapshot = TypedReads.Observed(label, NativeOps.ListWallUpgradeSites, reply, reply.Outcome?.Observed);
        s.RecordWire(label, snapshot);
        return snapshot;
    }

    /// <summary>Whether a site's normal is diagonal (a corner, replaced in place).</summary>
    internal static bool Corner(Observations.WallUpgradeSite site) => site.Normal is { X: not 0, Z: not 0 };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var walls = s.Staged!.Rooms["room"].Walls;
        var cleanup = await CensusAsync(s, "cleanup-census", null, cancellationToken).ConfigureAwait(false);
        foreach (var row in cleanup.Sites)
        {
            if (row.Replacement is null || row.CompletedBackups.Count == 0)
            {
                throw new AcceptanceException($"cleanup row without a permanent replacement and remaining backups: {row.Target?.Id}");
            }
        }

        var straight = 0;
        foreach (var wall in walls)
        {
            foreach (var row in (await CensusAsync(s, "census-" + wall, wall, cancellationToken).ConfigureAwait(false)).Sites)
            {
                Check(wall, row);
                straight += Corner(row) ? 0 : 1;
            }
        }

        if (straight == 0)
        {
            throw new AcceptanceException($"no wall of the staged room yielded a straight replacement site among {walls.Count} walls");
        }

        s.Record("straight_sites", straight);
        foreach (var (label, target, code) in new[] { ("blank-target", " ", Common.FailureCode.InvalidRequest), ("unknown-target", "Thing_NoSuchWall0", Common.FailureCode.NotFound) })
        {
            var reply = await s.CallAsync(label, NativeOps.ListWallUpgradeSites, new Observations.WallUpgradeSitesRequest { Scope = s.Scope, TargetId = target }, cancellationToken).ConfigureAwait(false);
            if (reply.Outcome?.Failure?.Code != code)
            {
                throw new AcceptanceException($"{label}: want {code}, got {reply.Outcome}");
            }
        }

        var after = await CensusAsync(s, "cleanup-census-after", null, cancellationToken).ConfigureAwait(false);
        if (after.Context != cleanup.Context)
        {
            throw new AcceptanceException($"the read-only pass moved the observation context: {cleanup.Context} -> {after.Context}");
        }
    }

    /// <summary>Asserts a per-target row is a complete replacement site of <paramref name="wall"/>.</summary>
    private static void Check(string wall, Observations.WallUpgradeSite row)
    {
        if (row.Target?.Id != wall || row.Original?.Id != wall || string.IsNullOrEmpty(row.TargetSnapshot?.Token) || row.TargetPresent != true)
        {
            throw new AcceptanceException($"{wall}: a replacement row must target the present original wall with a CAS token: {row}");
        }

        if (row.Normal is not { X: { } x, Z: { } z } || (x == 0 && z == 0))
        {
            throw new AcceptanceException($"{wall}: site without a normal: {row}");
        }

        if (string.IsNullOrEmpty(row.LeftSupport?.Id) || string.IsNullOrEmpty(row.RightSupport?.Id))
        {
            throw new AcceptanceException($"{wall}: site without both supports: {row}");
        }

        var want = Corner(row) ? 0 : 3;
        if (row.BackupCells.Count != want)
        {
            throw new AcceptanceException($"{wall}: {row.BackupCells.Count} backup cells, want {want} for normal {row.Normal}: {row}");
        }
    }
}
