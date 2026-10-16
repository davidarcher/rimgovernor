using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/home-coverage</c> (Go <c>home_coverage.go</c>): the fixture provides two empty bed chambers and a
/// connecting corridor. Both beds must be built through routine plans before Home can be maintained.
/// </summary>
internal static class UpkeepHomeCoverage
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/home-coverage",
        Scope = "Connected autonomous Home (#452): two routine-built beds, corridor coverage, stale revision/geometry refusal, restoration after removal and save recovery.",
        Start = new LabStart(),
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["sleeping", "home-coverage", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Reason = "Two small beds establish real ownership; subsequent Home writes require only rounds on the same colony.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        foreach (var fixture in new[] { "test/sleeping_setup", "test/home_coverage_setup", "test/home_coverage_read", "test/home_remove_cell", "test/home_cell_read" })
        {
            if (!Dyn.Contains(s.Names, fixture))
            {
                throw new AcceptanceException($"missing {fixture}");
            }
        }

        var h = s.Harness;
        var prepared = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_setup", new JsonObject { ["connectedRooms"] = true }, cancellationToken).ConfigureAwait(false);
        report["prepared"] = prepared;
        var before = await ReadBedIdsAsync(h, identity, "beds-before", cancellationToken).ConfigureAwait(false);
        var spec = s.Spec with { Families = ["sleeping", "work"] };
        var service = await s.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        var bedReport = new Report();
        report["stage_beds"] = bedReport.Data;
        try
        {
            using var journal = await ServeStageAsync(s, service, bedReport, cancellationToken).ConfigureAwait(false);
            await WatchHomeBedsAsync(journal, bedReport, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await ReadBedIdsAsync(h, identity, "beds-after", cancellationToken).ConfigureAwait(false);
        var built = new List<string>();
        foreach (var (id, def) in after)
        {
            if (!before.ContainsKey(id) && def == "Bed")
            {
                built.Add(id);
            }
        }

        built.Sort(StringComparer.Ordinal);
        report["beds_built"] = new JsonArray([.. built.Select(id => (JsonNode?)id)]);
        if (built.Count != 2)
        {
            throw new AcceptanceException($"expected two routine-built beds, got [{string.Join(' ', built)}]");
        }

        var corridor = prepared["corridor"].Obj();
        var margin = prepared["outside"].Obj();
        // The fixture point lies one cell past the east wall, inside the Home margin; the first cell excluded by
        // policy is a full margin farther east.
        if (!margin.ContainsKey("x"))
        {
            throw new AcceptanceException($"fixture outside point lacks coordinates: {prepared["outside"].ToJson()}");
        }

        var outside = new JsonObject { ["x"] = (int)margin["x"].AsNumber() + PolicyFunctions.HomeAreaMargin, ["z"] = (int)margin["z"].AsNumber() };
        report["home_margin_cell"] = margin;
        report["home_outside_margin"] = outside;
        var cx = (int)corridor["x"].AsNumber();
        var cz = (int)corridor["z"].AsNumber();
        var target = built[0];
        var staged = await UpkeepSupport.CallFixtureAsync(h, identity, "test/home_coverage_setup", new JsonObject { ["target"] = target }, cancellationToken).ConfigureAwait(false);
        report["stripped"] = staged;
        var total = (int)staged["count"].AsNumber();
        if (total < 9)
        {
            throw new AcceptanceException($"connected geometry omitted chambers or corridor: {staged.ToJson()}");
        }

        await AuditHomeCellsAsync(h, cx, cz, outside, false, cancellationToken).ConfigureAwait(false);
        for (var stage = 0; stage < 2; stage++)
        {
            var stageReport = new Report();
            report[string.Create(CultureInfo.InvariantCulture, $"stage_home_{stage}")] = stageReport.Data;
            if (stage == 1)
            {
                var removed = await UpkeepSupport.CallFixtureAsync(h, identity, "test/home_remove_cell", new JsonObject { ["target"] = target, ["x"] = cx + 1, ["z"] = cz }, cancellationToken).ConfigureAwait(false);
                report["removed"] = removed;
            }

            spec = s.Spec with { Families = ["home-coverage", "work"] };
            service = await s.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
            try
            {
                using var journal = await ServeStageAsync(s, service, stageReport, cancellationToken).ConfigureAwait(false);
                await WatchConnectedHomeAsync(journal, stageReport, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }

            h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
            foreach (var bed in built)
            {
                var covered = await ReadHomeCoverageAsync(h, bed, string.Create(CultureInfo.InvariantCulture, $"home-{stage}-{bed}"), cancellationToken).ConfigureAwait(false);
                stageReport[bed] = new JsonObject { ["covered"] = covered.Covered, ["total"] = covered.Total, ["revision"] = covered.Revision };
                if (covered.Covered != total || covered.Total != total)
                {
                    throw new AcceptanceException($"connected footprint not fully Home: {covered}, expected {total}");
                }
            }

            await AuditHomeCellsAsync(h, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
        }

        // Controller restarts above exercise durable routine ownership. Reload the final native save and
        // independently audit the actual Home mask as well.
        const string Save = "RimGovernor-home-connected";
        _ = await h.CallAsync("save-home", "rimgovernor/save_game", new JsonObject { ["saveName"] = Save }, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("reload-home", "rimgovernor/load_game_ready", new JsonObject
        {
            ["saveName"] = Save,
            ["readiness"] = "visual",
            ["timeoutMs"] = 90000,
            ["ignoreModCompatibility"] = false,
        }, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause-home", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        await AuditHomeCellsAsync(h, cx, cz, outside, true, cancellationToken).ConfigureAwait(false);
        report["save_recovery_home"] = true;
    }

    private static async Task WatchHomeBedsAsync(ControllerJournal journal, Report report, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(5));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var n = 0; n < 2; n++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"bed_{n}");
            await UpkeepSupport.FollowMethodsExcludingAsync(journal, PolicyFunctions.MaintainHousing, label, seen, a =>
                a.Building is { } b && b.Definition == "Bed" ? null : $"not a bed build: {a.Kind}", report, bounded.Token).ConfigureAwait(false);
            if (report[label + "_recovered_by"].AsString() != "controller_order")
            {
                throw new AcceptanceException($"bed {n} lacks completed controller ownership");
            }
        }
    }

    private static async Task WatchConnectedHomeAsync(ControllerJournal journal, Report report, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        await UpkeepSupport.WaitNeedRowAsync(journal, LayoutFamily.HomeCoverage, met: false, bounded.Token).ConfigureAwait(false);
        await UpkeepSupport.FollowMethodsAsync(journal, LayoutFamily.HomeCoverage.FilingKey, "home", a =>
            a.Area is { Home: true, Operation: AreaOperation.SetCells } ? null : "home method is not a home area set_cells", report, bounded.Token).ConfigureAwait(false);
        if (report["home_recovered_by"].AsString() != "controller_order")
        {
            throw new AcceptanceException("home extension lacks controller completion");
        }

        await UpkeepSupport.WaitNeedRowAsync(journal, LayoutFamily.HomeCoverage, met: true, bounded.Token).ConfigureAwait(false);
    }

    /// <summary>Audits the three corridor cells and the first cell beyond the margin against the native Home mask.</summary>
    internal static async Task AuditHomeCellsAsync(Harness h, int cx, int cz, JsonObject outside, bool want, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 4; i++)
        {
            int x = cx + i;
            int z = cz;
            var expected = want;
            if (i == 3)
            {
                x = (int)outside["x"].AsNumber();
                z = (int)outside["z"].AsNumber();
                expected = false;
            }

            var row = await h.CallAsync(
                string.Create(CultureInfo.InvariantCulture, $"home-cell-{i}-{(want ? "true" : "false")}"),
                "test/home_cell_read",
                new JsonObject { ["x"] = x, ["z"] = z },
                cancellationToken).ConfigureAwait(false);
            var actual = row["home"].AsBool();
            if (row["success"].AsBool() != true || actual is null || actual != expected)
            {
                throw new AcceptanceException($"home at {x},{z}: {row.ToJson()}, want {(expected ? "true" : "false")}");
            }
        }
    }

    /// <summary>
    /// Takes the service through authority to its first routine review, refusing a roll that holds development as an
    /// emergency; the returned journal is the caller's to dispose.
    /// </summary>
    internal static async Task<ControllerJournal> ServeStageAsync(ISession s, ServiceProcess service, Report report, CancellationToken cancellationToken)
    {
        var rootPlanId = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        report["root_plan"] = rootPlanId;
        service.KeepAuthority(cancellationToken);
        var journal = ControllerJournal.Open(s, service, cancellationToken);
        try
        {
            var (review, diagnostics) = await service.WaitRoundsAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            report["diagnostic_post_acquire"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.DeepClone())]);
            report["rounds_review_first"] = review.DeepClone();
            if (review["emergency"].AsSlice().Count > 0)
            {
                var stored = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
                var patients = string.Join(' ', stored.MedicalCare.Patients.Select(p => p.ToString()));
                throw new AcceptanceException($"first review holds development as an emergency (patients [{patients}]); the fixture roll is unusable");
            }
        }
        catch
        {
            journal.Dispose();
            throw;
        }

        return journal;
    }

    /// <summary>Maps every colonist bed on the map to its defName.</summary>
    internal static async Task<Dictionary<string, string>> ReadBedIdsAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var observed = await UpkeepSupport.ReadColonyFactsAsync(h, identity, label, cancellationToken).ConfigureAwait(false);
        var upkeep = UpkeepSupport.UpkeepSection(observed, label);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in upkeep["beds"].AsSlice())
        {
            var reference = raw.Obj()["bed"].Obj();
            var id = reference["id"].AsString();
            if (id.Length > 0)
            {
                ids[id] = reference["defName"].AsString();
            }
        }

        return ids;
    }

    internal sealed record HomeCoverageRead(int Covered, int Total, long Revision);

    /// <summary>The independent native Home read for one facility.</summary>
    internal static async Task<HomeCoverageRead> ReadHomeCoverageAsync(Harness h, string target, string label, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, "test/home_coverage_read", new JsonObject { ["target"] = target }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: no bounded native scope for {target}: {reply.ToJson()}");
        }

        return new HomeCoverageRead((int)reply["covered"].AsNumber(), (int)reply["total"].AsNumber(), (long)reply["revision"].AsNumber());
    }
}
