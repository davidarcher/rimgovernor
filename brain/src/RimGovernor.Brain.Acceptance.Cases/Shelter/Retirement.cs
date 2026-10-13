using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/retirement</c> (Go <c>shelter/retirement.go</c>): the whole shelter lifecycle's last act on a real game.
/// Everyone sleeps in a built bedroom (the layout grid fixture's expansion start), the planned workshop and laboratory stand
/// (staged on the plan the controller recorded) and the shelter still holds a research table, a sleeping spot, a crafting spot
/// and a butcher spot: the shelter retires and its footprint, table and spots included, is cleared through
/// <c>LayoutPlan.RetiredGround</c> until the entry drops; the table is packed, and the laboratory installs it from stock.
/// </summary>
internal static class ShelterRetirement
{
    private const string RetireTableDef = "SimpleResearchBench";

    private static readonly TimeSpan RetireWait = TimeSpan.FromMinutes(35);

    /// <summary>The shelter's loose spots, each of which the clearance of the retired ground must remove.</summary>
    private static readonly string[] RetireSpots = ["SleepingSpot", "CraftingSpot", "ButcherSpot"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "shelter/retirement",
        Scope = "Issue #2076: from the tribal " + Baselines.BaselineName + " baseline with every colonist asleep in a built bedroom, the planned " +
            "workshop and laboratory staged standing and a research table, sleeping spot, crafting spot and butcher spot left in the " +
            "shelter hut, the shelter retires: the plan drops it with its footprint as RetiredGround, and the table (packed, then installed " +
            "in a laboratory from stock), spots and hut walls are cleared before the entry leaves the plan. " +
            "A snapshot test cannot cover it: the clearance is pawns packing and deconstructing, and the " +
            "gate reads the native room census and sleeping facts.",
        Start = new FixtureStart
        {
            Op = LayoutFixture.GridPrepare,
            ArgsFrom = LayoutSiteHost.BedroomArgsAsync,
            Args = new JsonObject { ["sleepingSpots"] = 8, ["builders"] = true, ["expansion"] = true },
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        RequiredOps = [LayoutFixture.GridPrepare, LayoutFixture.GridAudit, ShelterStage.RoomsOp, DebugGame.LabSpawnTool],
        Keep = [NeedDef.Food.Value],
        Serve = new ServeSpec
        {
            Families = ["shelter", "sleeping", "clearance"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "shelter-retirement",
        },
        Budget = TimeSpan.FromMinutes(50),
        Crew = new Crew { Size = 3 },
        Reason = "three stages on one journal: the plan the controller records, the rooms staged on it, then pawn work (a Reinstall and a deconstruction of the hut) to the retirement's end",
        Run = RunAsync,
    };

    private sealed record Table(string Id, IReadOnlyList<Cell> Cells);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        report["fixture"] = prepared.DeepClone();

        // 1. The first plan holding the shelter, workshop and a demand-grown laboratory.
        var service = await ShelterShell.StartAsync(s, null, cancellationToken).ConfigureAwait(false);
        ShelterStage.PlanRecord record;
        try
        {
            record = await ShelterStage.WaitPlanAsync(
                s,
                service,
                ShelterStage.PlanWait,
                p => ShelterStage.RoomsOf(p, PolicyFunctions.PlannedShelter).Count == 1
                    && ShelterStage.RoomsOf(p, PolicyFunctions.PlannedWorkshop).Count > 0
                    && ShelterStage.RoomsOf(p, PolicyFunctions.PlannedLab).Count > 0,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            report["keepalive_plan"] = await service.StopAsync().ConfigureAwait(false);
            throw new AcceptanceException("wait for a plan with shelter, workshop and laboratory: " + e.Message, e);
        }

        report["keepalive_plan"] = await service.StopAsync().ConfigureAwait(false);
        var plan = record.Plan;
        var shelter = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedShelter)[0];
        var labs = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedLab);
        var workshops = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedWorkshop);
        var shelterBounds = shelter.Region.Bounds();
        report["plan"] = new JsonObject
        {
            ["tick"] = record.Tick,
            ["shelter"] = ShelterStage.RectJson(shelterBounds),
            ["workshops"] = workshops.Count,
            ["labs"] = labs.Count,
        };

        // 2. Stage the work rooms and leave a table and spots in the hut.
        var h = s.Reattach();
        List<PlannedRoom> work = [.. workshops, .. labs, shelter];
        var staged = await ShelterStage.StageRoomsAsync(h, "stage-work-rooms", work, cancellationToken).ConfigureAwait(false);
        report["staged_rooms"] = staged.DeepClone();
        var (table, spots) = await FurnishShelterAsync(s, h, prepared, shelterBounds, cancellationToken).ConfigureAwait(false);
        report["table"] = table.Id;
        report["spots"] = new JsonArray([.. spots.Select(id => (JsonNode?)JsonValue.Create(id))]);

        // 3. Retirement, to the entry's end.
        service = await ShelterShell.StartAsync(s, service, cancellationToken).ConfigureAwait(false);
        var ground = new List<Region>();
        LayoutPlan? final = null;
        try
        {
            using var journal = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            Exception? readError = null;
            AcceptanceException? waitError = null;
            try
            {
                _ = await ServiceJournal.WaitReviewAsync(
                    journal,
                    new Wait { Ceiling = RetireWait },
                    r =>
                    {
                        // A review before the first plan carries no plan identity yet.
                        if (!r.Snapshot.IsValid)
                        {
                            return false;
                        }

                        Store.LayoutPlanRecord? rec;
                        try
                        {
                            rec = journal.LayoutPlan(r.Snapshot, r.Tick, cancellationToken);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            readError = e;
                            return true;
                        }

                        if (rec?.Plan is not { } current)
                        {
                            return false;
                        }

                        final = current;
                        if (ground.Count == 0 && ShelterStage.RoomsOf(current, PolicyFunctions.PlannedShelter).Count == 0 && current.RetiredGround.Count > 0)
                        {
                            ground.AddRange(current.RetiredGround);
                        }

                        return ground.Count > 0 && current.RetiredGround.Count == 0;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                waitError = e;
            }

            report["retired_ground"] = new JsonArray([.. ground.Select(g => (JsonNode?)ShelterStage.RectJson(g.Bounds()))]);
            if (readError is not null)
            {
                throw new AcceptanceException(readError.Message, readError);
            }

            if (waitError is not null)
            {
                var planned = final is null ? 0 : ShelterStage.RoomsOf(final, PolicyFunctions.PlannedShelter).Count;
                var left = final is null ? 0 : final.RetiredGround.Count;
                throw new AcceptanceException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the shelter did not retire and clear (retired ground {ground.Count} regions, shelter planned {planned}, ground left {left} regions): {waitError.Message}"),
                    waitError);
            }
        }
        finally
        {
            report["keepalive_retire"] = await service.StopAsync().ConfigureAwait(false);
        }

        // 4. The game agrees: the table is not left in the hut (packed, or installed in a laboratory), the spots and the hut's walls are gone.
        h = s.Reattach();
        var (cells, found) = await ShelterStage.BuildingCellsAsync(h, s.Identity, "table-after", table.Id, cancellationToken).ConfigureAwait(false);
        report["table_after"] = ShelterStage.CellsJson(cells);
        if (found)
        {
            foreach (var c in cells)
            {
                if (ShelterStage.Inside(shelterBounds, c))
                {
                    throw new AcceptanceException($"research table {table.Id} still stands in the retired shelter at [{string.Join(' ', cells.Select(ShelterStage.Describe))}]");
                }
            }
        }

        for (var i = 0; i < spots.Count; i++)
        {
            var (_, spotFound) = await ShelterStage.BuildingCellsAsync(h, s.Identity, "spot-after", spots[i], cancellationToken).ConfigureAwait(false);
            if (spotFound)
            {
                throw new AcceptanceException($"{RetireSpots[i]} {spots[i]} still stands after the shelter's ground was cleared");
            }
        }

        var audit = await h.CallAsync("hut-audit", LayoutFixture.GridAudit, new JsonObject(), cancellationToken).ConfigureAwait(false);
        var remaining = new List<Cell>();
        foreach (var raw in audit["walls"].AsSlice())
        {
            var w = raw.AsMap() ?? [];
            var c = new Cell((int)w["x"].AsNumber(), (int)w["z"].AsNumber());
            foreach (var g in ground)
            {
                if (g.Contains(c) && !KeptByRoom(final!, c))
                {
                    remaining.Add(c);
                }
            }
        }

        report["walls_left_on_retired_ground"] = ShelterStage.CellsJson(remaining);
        if (remaining.Count > 0)
        {
            throw new AcceptanceException($"{remaining.Count} wall or door cells of the retired shelter still stand: [{string.Join(' ', remaining.Select(ShelterStage.Describe))}]");
        }
    }

    /// <summary>Whether <paramref name="c"/> is on a planned room's interior or wall ring.</summary>
    private static bool KeptByRoom(LayoutPlan plan, Cell c)
    {
        foreach (var r in plan.Rooms)
        {
            var inner = r.Region.Bounds();
            if (ShelterStage.Inside(new Rectangle(inner.X - 1, inner.Z - 1, inner.Width + 2, inner.Height + 2), c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Spawns a research table and one of each loose spot inside the planned shelter interior (staged with the work rooms; the
    /// plan sites it wherever it likes, not on the fixture hut), clear of the fixture campfire, and returns the table and the
    /// spots' ids in <see cref="RetireSpots"/> order.
    /// </summary>
    private static async Task<(Table Table, List<string> Spots)> FurnishShelterAsync(ISession s, Harness h, JsonObject prepared, Rectangle planned, CancellationToken cancellationToken)
    {
        // The plan's shelter need not overlap the fixture hut: furnish the planned interior the stage raised.
        var room = planned;
        var expansion = prepared["expansion"].AsMap() ?? [];
        var campfire = expansion["campfire"].AsString();
        var fire = ParseCampfire(campfire);
        var taken = new HashSet<Cell> { fire };

        // The table is 3x2, anchored (SimpleResearchBench, north) at its middle column and bottom row.
        Cell anchor = default;
        var placed = false;
        for (var z = room.Z; z + 1 < room.Z + room.Height && !placed; z++)
        {
            for (var x = room.X; x + 2 < room.X + room.Width && !placed; x++)
            {
                var clear = true;
                for (var dz = 0; dz < 2; dz++)
                {
                    for (var dx = 0; dx < 3; dx++)
                    {
                        clear = clear && !taken.Contains(new Cell(x + dx, z + dz));
                    }
                }

                if (clear)
                {
                    anchor = new Cell(x + 1, z);
                    placed = true;
                }
            }
        }

        if (!placed)
        {
            throw new AcceptanceException($"no 3x2 floor for the research table in the planned shelter {ShelterStage.Describe(planned)}");
        }

        var (id, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = RetireTableDef, X = anchor.X, Z = anchor.Z }, cancellationToken).ConfigureAwait(false);
        var (cells, found) = await ShelterStage.BuildingCellsAsync(h, s.Identity, "table-before", id, cancellationToken).ConfigureAwait(false);
        if (!found || cells.Count != 6)
        {
            throw new AcceptanceException($"research table {id} occupies [{string.Join(' ', cells.Select(ShelterStage.Describe))}], want six cells");
        }

        foreach (var c in cells)
        {
            taken.Add(c);
            if (!ShelterStage.Inside(planned, c))
            {
                throw new AcceptanceException($"research table cell {ShelterStage.Describe(c)} lies outside the planned shelter {ShelterStage.Describe(planned)}");
            }
        }

        var ids = new List<string>();
        foreach (var def in RetireSpots)
        {
            var spawned = false;
            for (var z = room.Z; z < room.Z + room.Height && !spawned; z++)
            {
                for (var x = room.X; x < room.X + room.Width && !spawned; x++)
                {
                    var c = new Cell(x, z);
                    if (taken.Contains(c))
                    {
                        continue;
                    }

                    var (spotId, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = def, X = x, Z = z }, cancellationToken).ConfigureAwait(false);
                    taken.Add(c);
                    spawned = true;
                    ids.Add(spotId);
                }
            }

            if (!spawned)
            {
                throw new AcceptanceException($"no free shelter cell for a {def}");
            }
        }

        return (new Table(id, cells), ids);
    }

    /// <summary>Reads the fixture campfire's "(x, y, z)" cell text.</summary>
    private static Cell ParseCampfire(string text)
    {
        var parts = text.Trim().TrimStart('(').TrimEnd(')').Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
        {
            throw new AcceptanceException($"fixture campfire cell \"{text}\": input does not match format");
        }

        return new Cell(x, z);
    }
}
