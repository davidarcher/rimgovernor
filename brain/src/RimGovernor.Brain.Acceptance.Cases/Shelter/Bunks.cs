using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRounds = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/bunks-first</c> (Go <c>shelter/bunks.go</c>): the one complete-construction path. Sleeping spots, then the
/// beds on the freed slots, then the ring around them, all by ordinary pawn work.
/// </summary>
internal static class ShelterBunks
{
    /// <summary>The WoodLog dropped beside a colonist before the service starts (the baseline's 500 covers the beds but not the ring after them).</summary>
    private const int BunkWood = 1000;

    // buildingruntime's shelter method ids (the Cases project does not reference BuildingRuntime).
    private const string SpotsMethod = "shelter-spots";
    private const string BedsMethod = "shelter-beds";
    private const string ClearBedsMethod = "shelter-clear-beds";

    /// <summary>One 1x2 bunk a rung placed, with its completion tick.</summary>
    private sealed record Bunk(IReadOnlyList<Cell> Cells, long Tick);

    internal static AcceptanceCase Case() => new()
    {
        Name = "shelter/bunks-first",
        Scope = "Issues #612, #615 and #2278: the one complete-construction path. From the tribal " + Baselines.BaselineName +
            " baseline, which houses nobody indoors at the start (asserted), the initial shelter places sleeping spots at " +
            "the first review on the planned shelter interior, admits the wooden beds next on the same slots and raises the ring (the planned room's waves) around them without waiting for the beds " +
            "to stand (#641) -- every wall and the door by ordinary pawn work, nothing staged; " +
            "the game roofs the room and the native census then holds one bed per colonist inside it.",
        Start = new SaveStart { Name = Baselines.BaselineName },
        Serve = new ServeSpec { Families = ShelterShell.Families, NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "bunks" },
        Budget = TimeSpan.FromMinutes(25),
        Crew = new Crew { Size = 3 },
        Reason = "one unstaged run of three construction rungs: eight tribal builders raise eight beds (800 work each) and a hut ring of some thirty cells side by side, and the game roofs the room after; the rungs' layout and progress are the assertion, so no rung can be staged",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        await ShelterShell.AllowSuppliesAsync(h, "allow-supplies", report, cancellationToken).ConfigureAwait(false);

        // The precondition must not already satisfy the outcome: the colony starts with no roofed room holding a bed.
        var (colonists, beside) = await UnhousedColonyAsync(s, report, cancellationToken).ConfigureAwait(false);

        // Wood for the ring, dropped before the service starts.
        var dropped = await h.CallAsync(
            "drop-wood",
            "test/hut_shell_fixture",
            new JsonObject
            {
                ["action"] = "wood",
                ["door"] = string.Create(CultureInfo.InvariantCulture, $"{beside.X},{beside.Z}"),
                ["wood"] = BunkWood,
            },
            cancellationToken).ConfigureAwait(false);
        report["dropped_wood"] = dropped;

        // The baseline has not researched ComplexFurniture, so the beds rung would be refused.
        var furniture = await h.CallAsync("finish-furniture", "test/hut_shell_fixture", new JsonObject { ["action"] = "furniture" }, cancellationToken).ConfigureAwait(false);
        report["furniture"] = furniture;

        // The baseline already stands a few walls, which may sit on the ring the planner sites.
        var standing = await StandingWallsAsync(s, report, cancellationToken).ConfigureAwait(false);
        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        Journal st;
        try
        {
            st = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        using (st)
        {
            try
            {
                var stopped = await RungsAsync(s, service, st, standing, colonists, report, cancellationToken).ConfigureAwait(false);
                if (!stopped)
                {
                    _ = await service.StopAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                _ = await service.StopAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    // Runs the rungs up to stopping the service; returns true once the service is stopped, so the caller's stop is idempotent.
    private static async Task<bool> RungsAsync(
        ISession s,
        ServiceProcess service,
        Journal st,
        HashSet<Cell> standing,
        int colonists,
        Report report,
        CancellationToken cancellationToken)
    {
        // 1. Sleeping spots at the first review, before any wall blueprint.
        var spots = await WaitBunksAsync(st, SpotsMethod, "SleepingSpot", false, ShelterShell.WaitOf(ShelterShell.BuildWait, service), cancellationToken).ConfigureAwait(false);
        report["spots"] = DescribeBunks(spots);
        NoShellYet(st, "sleeping spots", cancellationToken);

        // 2. Once the spots stand they are deleted (the native refuses a bed over a standing spot), then the beds go on the freed slots.
        var deleted = await WaitSpotsClearedAsync(st, ShelterShell.WaitOf(ShelterShell.BuildWait, service), cancellationToken).ConfigureAwait(false);
        report["spots_deleted"] = ShelterStage.CellsJson(deleted);
        var placed = await WaitBunksAsync(st, BedsMethod, "Bed", false, ShelterShell.WaitOf(ShelterShell.BuildWait, service), cancellationToken).ConfigureAwait(false);
        if (placed.Count != spots.Count)
        {
            throw new AcceptanceException($"{placed.Count} beds placed on {spots.Count} spot slots");
        }

        for (var i = 0; i < placed.Count; i++)
        {
            if (CellsText(placed[i].Cells) != CellsText(spots[i].Cells))
            {
                throw new AcceptanceException($"bed {i} on {CellsText(placed[i].Cells)}, not on the freed spot slot {CellsText(spots[i].Cells)}");
            }
        }

        var sh = await ShelterShell.WaitShellAsync(st, ShelterShell.WaitOf(ShelterShell.BuildWait, service), cancellationToken).ConfigureAwait(false);
        report["shell"] = sh.Describe();

        // The spots and beds were furnished from the plan before any wall: every one stands on the planned interior.
        var planned = new HashSet<Cell>(sh.Footprint.Interior);
        foreach (var rung in new[] { spots, placed })
        {
            foreach (var b in rung)
            {
                foreach (var c in b.Cells)
                {
                    if (!planned.Contains(c))
                    {
                        throw new AcceptanceException($"bunk cell {ShelterStage.Describe(c)} lies outside the planned shelter interior {sh.BoundsText()}");
                    }
                }
            }
        }

        // 3. Every wall, the door and every bed is placed (built natively below).
        await ShelterShell.WaitLineageAsync(st, sh, ShelterShell.WaitOf(ShelterShell.BuildWait, service), l => l.Missing(sh.Cells, standing) == 0, cancellationToken).ConfigureAwait(false);
        var beds = await WaitBunksAsync(st, BedsMethod, "Bed", true, ShelterShell.WaitOf(ShelterShell.BuildWait, service), cancellationToken).ConfigureAwait(false);
        report["beds"] = DescribeBunks(beds);
        var bedCells = new List<Cell>();
        foreach (var b in beds)
        {
            foreach (var c in b.Cells)
            {
                if (!planned.Contains(c))
                {
                    throw new AcceptanceException($"bed cell {ShelterStage.Describe(c)} lies outside the sited shell {sh.BoundsText()}");
                }

                bedCells.Add(c);
            }
        }

        report["keepalive"] = await service.StopAsync().ConfigureAwait(false);

        // 4. Roofed, every colonist housed: the native room holds a bed per colonist, all inside.
        var h = s.Reattach();
        await ShelterShell.ReleaseServiceAsync(h, s.Identity, report, cancellationToken).ConfigureAwait(false);
        await ShelterShell.WaitBuiltAsync(h, s.Identity, sh, bedCells, report, cancellationToken).ConfigureAwait(false);
        await ShelterShell.VerifyNativeAsync(h, s.Identity, sh, standing, bedCells, report, cancellationToken).ConfigureAwait(false);
        var listed = await Colonists.ListColonistsAsync(h, "colonists-after", false, false, cancellationToken).ConfigureAwait(false);
        var after = listed.Count;
        if (after != colonists)
        {
            throw new AcceptanceException($"the colony changed size during the run: {after} colonists, {colonists} before");
        }

        // Capacity for this fixture: one bed per colonist, all of them inside the one roofed room verifyNative checked.
        var housed = (report["native_beds"] as JsonArray)?.Count ?? 0;
        report["colonists"] = colonists;
        report["housed"] = housed;
        if (colonists == 0 || housed < colonists)
        {
            throw new AcceptanceException($"{housed} beds in the roofed hut for {colonists} colonists");
        }

        return true;
    }

    /// <summary>The cells holding a built wall before the service starts.</summary>
    private static async Task<HashSet<Cell>> StandingWallsAsync(ISession s, Report report, CancellationToken cancellationToken)
    {
        var grid = await ShelterShell.ThingGrid.ReadAsync(s.Harness, "walls-before", s.Identity, cancellationToken).ConfigureAwait(false);
        var standing = new HashSet<Cell>();
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            foreach (var (def, flags) in grid.Things[i])
            {
                if (def == PolicyFunctions.ShellWallDefinition && (flags & (uint)ThingMarks.Edifice) != 0)
                {
                    standing.Add(grid.Cells[i].Cell);
                }
            }
        }

        report["standing_walls"] = standing.Count;
        return standing;
    }

    /// <summary>
    /// Reads the colony before the controller starts and returns its colonist count and the first colonist's cell, refusing
    /// a baseline that already meets the outcome: no native room may be a proper roofed indoor room holding a bed.
    /// </summary>
    private static async Task<(int Colonists, Cell Beside)> UnhousedColonyAsync(ISession s, Report report, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var reply = await h.WireAsync(
            "rooms-before",
            "observations_list_rooms",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        int rooms = 0, indoorBeds = 0;
        foreach (var raw in observed["rooms"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["properRoom"].AsBool() != true || row["outdoors"].AsBool() == true || row["openRoofCount"].AsNumber() != 0)
            {
                continue;
            }

            rooms++;
            indoorBeds += row["beds"].AsSlice().Count;
        }

        var listed = await Colonists.ListColonistsAsync(h, "colonists-before", false, false, cancellationToken).ConfigureAwait(false);
        var colonists = listed.Count;
        report["precondition"] = new JsonObject { ["colonists"] = colonists, ["roofed_rooms"] = rooms, ["indoor_beds"] = indoorBeds };
        if (colonists == 0)
        {
            throw new AcceptanceException("the baseline has no colonists to shelter");
        }

        if (indoorBeds > 0)
        {
            throw new AcceptanceException($"the baseline already holds {indoorBeds} beds in {rooms} roofed rooms: the precondition satisfies the outcome");
        }

        var first = listed[0]["pawn"].AsMap() ?? [];
        var position = first["position"].AsMap() ?? [];
        return (colonists, new Cell((int)position["x"].AsNumber(), (int)position["z"].AsNumber()));
    }

    /// <summary>
    /// Polls the store until the shelter goal has bound the named bunk rung (live or already retired) and, when
    /// <paramref name="complete"/> is set, until every bunk of it is completed.
    /// </summary>
    private static async Task<IReadOnlyList<Bunk>> WaitBunksAsync(Journal st, string method, string definition, bool complete, Wait w, CancellationToken cancellationToken)
    {
        IReadOnlyList<Bunk> found = [];
        var seen = string.Empty;
        try
        {
            await w.WaitProgressAsync(
                async ct =>
                {
                    ReviewRounds review;
                    try
                    {
                        review = await st.LoadRoundsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-review", e.Message), false);
                    }

                    if (review.Revision != 0)
                    {
                        var owner = st.LoadNeed(ShelterFamily.Starter, review.Snapshot, ct);

                        // The latest binding in any epoch, retired or not: a bunk rung completes on its placement receipt and retires at once.
                        if (st.OwnerMethodPlan(owner, new MethodId(method), ct) is not { } id)
                        {
                            return (Wait.Signature(ShelterFamily.Starter.Value, "unbound"), false);
                        }

                        var plan = await st.LoadPlanAsync(id, ct).ConfigureAwait(false);
                        var bunks = new List<Bunk>();
                        var done = true;
                        for (var i = 0; i < plan.Spec.Actions.Count; i++)
                        {
                            var b = plan.Spec.Actions[i].Building;
                            if (b is null || b.Definition != definition)
                            {
                                throw new AcceptanceException($"{method} places {plan.Spec.Actions[i].Kind}, want {definition}");
                            }

                            var f = PolicyFunctions.BunkCells(b.Cell, b.Rotation);
                            var v = plan.Progress[i].View;
                            seen = $"{plan.Spec.Id.Value} at {ShelterStage.Describe(b.Cell)} stage {v.Stage}";
                            done = done && v.Stage == Stage.Completed;
                            bunks.Add(new Bunk([.. f], v.Tick.Value));
                        }

                        if (bunks.Count == 0)
                        {
                            throw new AcceptanceException($"{method} admitted no bunk");
                        }

                        if (complete && !done)
                        {
                            return (Wait.Signature(ShelterFamily.Starter.Value, seen), false);
                        }

                        found = bunks;
                        return ("", true);
                    }

                    return (Wait.Signature("unbound", review.Revision), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{method} not admitted for the starter shelter Need (last seen: {seen}): {e.Message}", e);
        }

        return found;
    }

    /// <summary>Fails when any shell plan is on the journal: the ring must wait for the bunks.</summary>
    private static void NoShellYet(Journal st, string after, CancellationToken cancellationToken)
    {
        foreach (var plan in st.LoadPlans(cancellationToken))
        {
            if (Store.StorePackage.IsRoomShellMethod(plan.Method))
            {
                throw new AcceptanceException($"shell plan {plan.Spec.Id.Value} admitted before the {after}");
            }
        }
    }

    /// <summary>
    /// Polls the store until the shelter goal has bound the spot deletion the bed rung runs first and returns the cells of the
    /// spots it deletes.
    /// </summary>
    private static async Task<IReadOnlyList<Cell>> WaitSpotsClearedAsync(Journal st, Wait w, CancellationToken cancellationToken)
    {
        var cells = new List<Cell>();
        try
        {
            await w.WaitProgressAsync(
                async ct =>
                {
                    ReviewRounds review;
                    try
                    {
                        review = await st.LoadRoundsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-review", e.Message), false);
                    }

                    if (review.Revision != 0)
                    {
                        var owner = st.LoadNeed(ShelterFamily.Starter, review.Snapshot, ct);

                        if (st.OwnerMethodPlan(owner, new MethodId(ClearBedsMethod), ct) is not { } id)
                        {
                            return (Wait.Signature(ShelterFamily.Starter.Value, "unbound"), false);
                        }

                        var plan = await st.LoadPlanAsync(id, ct).ConfigureAwait(false);
                        cells.Clear();
                        for (var i = 0; i < plan.Spec.Actions.Count; i++)
                        {
                            var cut = plan.Spec.Actions[i].Deconstruction;
                            if (cut is null || cut.Definition != "SleepingSpot")
                            {
                                throw new AcceptanceException($"{ClearBedsMethod} places {plan.Spec.Actions[i].Kind}, want a SleepingSpot deletion");
                            }

                            cells.Add(cut.Cell);
                        }

                        return ("", true);
                    }

                    return (Wait.Signature("unbound", review.Revision), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{ClearBedsMethod} not admitted for the starter shelter Need: {e.Message}", e);
        }

        return cells;
    }

    private static string CellsText(IReadOnlyList<Cell> cells) => "[" + string.Join(' ', cells.Select(ShelterStage.Describe)) + "]";

    private static JsonArray DescribeBunks(IReadOnlyList<Bunk> bunks) =>
        new([.. bunks.Select(b => (JsonNode?)new JsonObject { ["cells"] = ShelterStage.CellsJson(b.Cells), ["tick"] = b.Tick })]);
}
