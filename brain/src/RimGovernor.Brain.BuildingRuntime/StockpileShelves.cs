namespace RimGovernor.Brain.BuildingRuntime;

internal sealed partial class RoundsStockpilePlanner
{
    /// <summary>
    /// The stockpiles step's shelf upgrade: the review's next warehouse shelf, placed once no zone edit stands.
    /// NextShelfStep picked the step (one open shelf at a time, a third of the footprint at most); the
    /// admin/stockpiles Need configures every built shelf like its zone (StockpileShelfPatch, role shelf:buildingId).
    /// </summary>
    private async Task<Verdict> ShelveAsync(NeedWork work, Sto.Rounds review, Observation.RoundsReading reading, Pol.ShelfStep step, CancellationToken call)
    {
        if (_building is null)
        {
            return FieldUnavailable("building_source");
        }
        var zone = step.Zone?.Zone ?? throw new InvalidOperationException("a shelf step names the zone it shelves");
        var (_, index) = ZoneShelves(_reviewer.Player.Journal, work.Owner, zone, reading.Projection.Facts.CurrentConstruction, call);
        if (index >= MaxShelvesPerZone)
        {
            return Refuse(Pol.Cause.RetriesSpent, "maxShelvesPerZone", "");
        }
        return await BuildShelfAsync(work, review, reading, step, index, call).ConfigureAwait(false);
    }

    /// <summary>Previews the step's candidate sites in turn and admits the first native accepts as the zone's next shelf.</summary>
    private async Task<Verdict> BuildShelfAsync(NeedWork work, Sto.Rounds review, Observation.RoundsReading reading, Pol.ShelfStep step, int index, CancellationToken call)
    {
        var shelfZone = step.Zone?.Zone ?? throw new InvalidOperationException("a shelf step names the zone it shelves");
        var p = _reviewer.Player;
        var state = work.State;
        var goal = work.Owner;
        var epoch = work.Epoch;
        var facts = reading.Projection;
        var stuff = facts.BuildStuff(ShelfDefinition);
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("build: p.session.State() != state");
            }
        }
        var building = new RoundsBuildingPlanner { Reviewer = _reviewer, Native = _building!.Native, Concern = AdminFamily.Stockpiles.FilingKey, Need = AdminFamily.Stockpiles, Definition = ShelfDefinition };
        foreach (var piece in step.Pieces)
        {
            Check();
            var value = new Building(piece.Def, piece.Anchor(), piece.Rot, stuff);
            // A footprint on rock or fogged mountain is mined and the shelf built in one method through the shared rock step.
            var planned = new List<Pol.RoleCell>();
            for (var x = piece.Rect.X; x < piece.Rect.X + piece.Rect.Width; x++)
            {
                for (var z = piece.Rect.Z; z < piece.Rect.Z + piece.Rect.Height; z++)
                {
                    planned.Add(new Pol.RoleCell { Cell = new Cell(x, z), Role = Pol.RockRole.NeedsFloor });
                }
            }
            var dig = Pol.PolicyFunctions.RockStep(planned, facts.Cells).Dig;
            if (dig.Count > 0)
            {
                var (access, ok) = Pol.PolicyFunctions.RockAccess(dig, facts.Cells);
                if (!ok)
                {
                    return RockNotDug(ShelfDefinition, "no_open_cell_beside_footprint");
                }
                var rockMethod = ShelfMethod(shelfZone, index);
                var (dug, handled) = await building.AdmitRockStepAsync(new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts, Read = reading.Reading }, planned, access, rockMethod, [value], Check, call, epoch).ConfigureAwait(false);
                if (handled)
                {
                    return dug.Verdict;
                }
            }
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-0")), value, ConstructionTier.Sustain);
            var (preview, _) = await _building.Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var v = preview.Preview;
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            if (!ck || !can || !sk || !safe)
            {
                continue;
            }
            var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
            stock = MergeRoundsStock(stock, preview.Stock, true);
            var method = ShelfMethod(shelfZone, index);
            var result = await building.AdmitPreviewsAsync(new RoundsAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Reason = "shelf for zone " + shelfZone, Snapshot = snapshot, Selected = [v], Stock = stock, Purpose = Pol.Purpose.Rounds }, call, epoch).ConfigureAwait(false);
            return result.Verdict;
        }
        return NoSpace("storage_shelf");
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Bounds the shelves a zone may ever be given.</summary>
    internal const int MaxShelvesPerZone = 8;

    /// <summary>Keys a zone shelf by its zone and index: the shelf it names.</summary>
    internal static MethodId ShelfMethod(string zone, int index) =>
        new(string.Create(CultureInfo.InvariantCulture, $"storage-shelf-{zone}-{index}"));

    /// <summary>
    /// Reads back the zone's shelf plans in index order: the shelves built or in flight, and the next free index.
    /// A shelf is the admin/stockpiles Need's latest attempt of its MethodId.
    /// </summary>
    internal static (List<Pol.ShelfRecord> Shelves, int Next) ZoneShelves(StoreJournal journal, Sto.NeedOwner need, string zone, Fact<Pol.CurrentConstruction> census, CancellationToken ct)
    {
        var output = new List<Pol.ShelfRecord>();
        for (var index = 0; index < MaxShelvesPerZone; index++)
        {
            var method = ShelfMethod(zone, index);
            if (journal.OwnerMethodPlan(need, method, ct) is not { } id)
            {
                return (output, index);
            }
            var plan = journal.LoadPlan(id, ct);
            var record = new Pol.ShelfRecord { Zone = zone };
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Building is not { } b)
                {
                    continue;
                }
                var rect = Pol.PolicyFunctions.OccupiedRect(b.Cell, new Cell(2, 1), b.Rotation);
                var cells = new List<Cell>();
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    for (var z = rect.Z; z < rect.Z + rect.Height; z++)
                    {
                        cells.Add(new Cell(x, z));
                    }
                }
                record = record with { Cells = cells };
            }
            if (StorePackage.PlanWorkOpen(plan, census))
            {
                record = record with { Open = true };
                output.Add(record);
                continue;
            }
            if (!Pol.PolicyFunctions.PlanBuilt(plan.Progress, census))
            {
                continue;
            }
            census.TryGetValue(out var current);
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Building is { } b && current is not null && current.Built(b) is { } builtId)
                {
                    record = record with { Building = builtId };
                }
            }
            if (record.Building == "")
            {
                continue;
            }
            output.Add(record);
        }
        return (output, MaxShelvesPerZone);
    }
}
