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
        Scope = "Runtime extent producer leaves the complete native Home mask byte-identical; Home maintenance covers the ready corridor and base margin, leaving cells beyond the margin outside Home.",
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

        async Task RecordHomeAsync(string stage, bool wantMargin)
        {
            var mask = await h.CallAsync(stage + "-mask", "test/home_mask", null, cancellationToken).ConfigureAwait(false);
            var cell = await h.CallAsync(stage + "-outside", "test/home_cell_read", outside.DeepClone().AsObject(), cancellationToken).ConfigureAwait(false);
            var marginCell = await h.CallAsync(stage + "-margin", "test/home_cell_read", margin.DeepClone().AsObject(), cancellationToken).ConfigureAwait(false);
            s.Report[stage] = new JsonObject { ["mask"] = mask["mask"]?.DeepClone(), ["outside"] = cell, ["margin"] = marginCell };
            var outsideHome = cell["home"].AsBool();
            var marginHome = marginCell["home"].AsBool();
            var outsideOk = cell["success"].AsBool() == true;
            var marginOk = marginCell["success"].AsBool() == true;
            if (!outsideOk || !marginOk || outsideHome is null || marginHome is null || outsideHome == true || marginHome != wantMargin)
            {
                throw new AcceptanceException($"{stage}: outside Home={cell.ToJson()}, margin Home={marginCell.ToJson()} (want {(wantMargin ? "true" : "false")})");
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
        await UpkeepHomeCoverage.AuditHomeCellsAsync(h, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
        var before = await h.CallAsync("extent-home-before", "test/home_mask", null, cancellationToken).ConfigureAwait(false);
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
        var after = await h.CallAsync("extent-home-after", "test/home_mask", null, cancellationToken).ConfigureAwait(false);
        var mask = before["mask"].AsString();
        if (mask.Length == 0 || mask != after["mask"].AsString())
        {
            throw new AcceptanceException("extent changes mutated the native Home mask");
        }

        s.Report["home_mask_byte_identical"] = true;
        await UpkeepHomeCoverage.AuditHomeCellsAsync(h, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
    }
}
