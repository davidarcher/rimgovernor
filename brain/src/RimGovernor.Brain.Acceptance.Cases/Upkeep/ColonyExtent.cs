using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>upkeep/colony-extent</c> (Go <c>colony_extent.go</c>).</summary>
internal static class UpkeepColonyExtent
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/colony-extent",
        Scope = "Runtime extent producer leaves the complete native Home mask (every held cell's Home bit) unchanged; Home maintenance covers the ready corridor and base margin, leaving cells beyond the margin outside Home.",
        Start = new LabStart(),
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["home-coverage", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Reason = "Ready connected rooms; Home orders require no construction waits.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var prepared = await UpkeepSupport.CallFixtureAsync(h, s.Identity, "test/sleeping_setup", new JsonObject { ["connectedRooms"] = true }, cancellationToken).ConfigureAwait(false);
        s.Report["prepared"] = prepared;
        var corridor = prepared["corridor"].Obj();
        var margin = prepared["outside"].Obj();
        // The fixture point is one cell beyond the east wall, inside the Home margin. The first excluded cell is a
        // full margin farther east.
        var outside = new JsonObject { ["x"] = (int)margin["x"].AsNumber() + PolicyFunctions.HomeAreaMargin, ["z"] = (int)margin["z"].AsNumber() };
        s.Report["home_margin_cell"] = margin;
        s.Report["home_outside_margin"] = outside;
        var cx = (int)corridor["x"].AsNumber();
        var cz = (int)corridor["z"].AsNumber();

        var outsideCell = new Cell((int)outside["x"].AsNumber(), (int)outside["z"].AsNumber());
        var marginAt = new Cell((int)margin["x"].AsNumber(), (int)margin["z"].AsNumber());

        // The held cells in Home over the whole map, read through the cells read.
        async Task<HashSet<Cell>> HomeMaskAsync(string label) =>
            [.. (await h.MapCellsAsync(label, s.Identity, cancellationToken).ConfigureAwait(false)).Where(c => c.InHome)];

        async Task RecordHomeAsync(string stage, bool wantMargin)
        {
            var grid = await h.MapCellsAsync(stage + "-cells", s.Identity, cancellationToken).ConfigureAwait(false);
            var outsideHome = grid.At(outsideCell)?.InHome;
            var marginHome = grid.At(marginAt)?.InHome;
            s.Report[stage] = new JsonObject { ["home_cells"] = grid.Where(c => c.InHome).Count, ["outside"] = outsideHome, ["margin"] = marginHome };
            if (outsideHome is null || marginHome is null || outsideHome == true || marginHome != wantMargin)
            {
                throw new AcceptanceException($"{stage}: outside Home={outsideHome}, margin Home={marginHome} (want {(wantMargin ? "true" : "false")})");
            }
        }

        await RecordHomeAsync("home_after_sleeping_setup", false).ConfigureAwait(false);

        var ready = await UpkeepSupport.CallFixtureAsync(h, s.Identity, "test/extent_home_prepare", new JsonObject { ["x"] = cx, ["z"] = cz }, cancellationToken).ConfigureAwait(false);
        s.Report["ready_home"] = ready;
        await RecordHomeAsync("home_before_maintenance", false).ConfigureAwait(false);
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            using var journal = await UpkeepHomeCoverage.ServeStageAsync(s, service, s.Report, cancellationToken).ConfigureAwait(false);
            await UpkeepSupport.FollowMethodsAsync(journal, LayoutFamily.HomeCoverage.FilingKey, "home", _ => null, s.Report, cancellationToken).ConfigureAwait(false);
            await UpkeepSupport.WaitNeedRowAsync(journal, LayoutFamily.HomeCoverage, met: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        await RecordHomeAsync("home_after_maintenance", true).ConfigureAwait(false);
        await UpkeepHomeCoverage.AuditHomeCellsAsync(h, s.Identity, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
        var before = await HomeMaskAsync("extent-home-before").ConfigureAwait(false);
        service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            using var journal = await UpkeepHomeCoverage.ServeStageAsync(s, service, s.Report, cancellationToken).ConfigureAwait(false);
            var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
            var history = await journal.Store.EstablishedColonyExtentAsync(review.Snapshot, review.Tick, cancellationToken).ConfigureAwait(false);
            if (history.Count == 0)
            {
                throw new AcceptanceException($"runtime extent missing: {history.Count} regions");
            }

            var occupied = new HashSet<Cell>();
            foreach (var row in history)
            {
                foreach (var c in row.Region.Cells)
                {
                    occupied.Add(c.Cell);
                }
            }

            if (!occupied.Contains(new Cell(cx, cz)))
            {
                throw new AcceptanceException("runtime extent omitted observed corridor");
            }

            s.Report["established_regions"] = history.Count;
            s.Report["extent_cells"] = occupied.Count;
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await HomeMaskAsync("extent-home-after").ConfigureAwait(false);
        if (before.Count == 0 || !before.SetEquals(after))
        {
            throw new AcceptanceException($"extent changes mutated the native Home mask: {before.Count} Home cells before, {after.Count} after, {before.Count(after.Contains)} shared");
        }

        s.Report["home_mask_identical"] = true;
        s.Report["home_mask_cells"] = before.Count;
        await UpkeepHomeCoverage.AuditHomeCellsAsync(h, s.Identity, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
    }
}
