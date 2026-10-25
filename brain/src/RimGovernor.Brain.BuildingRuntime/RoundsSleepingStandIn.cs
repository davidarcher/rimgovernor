namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// The day-one floor (#3659): while the foothold sleeping and indoor places are not both met, places the missing
    /// stand-in sleeping spots (ShelterRules.StandInsOwed) on the first selected sleeping room's free interior and the
    /// cells next to its ring (ShelterRules.StandInAnchors, footprints apart: PickStandIns), outdoors allowed, as one Survive-tier Method per shortfall.
    /// Null when nothing is owed, no sleeping room is planned, a stand-in Method is still open, the shortfall's Method
    /// already ran, no candidate cell is placeable or the Method is refused: the sleeping ladder then goes on as usual.
    /// </summary>
    internal async Task<RoundsBuildingResult?> StandInSpotsAsync(ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.ColonyProjection facts, CancellationToken call, CancellationToken epoch)
    {
        var (count, missing) = ShelterRules.StandInsOwed(facts.Facts);
        if (missing == 0 || facts.LayoutPlan is not { } plan)
        {
            return null;
        }
        var p = Reviewer.Player;
        foreach (var m in goal.OpenMethods)
        {
            if (m.Method.Value.StartsWith(ShelterRules.StandInMethodPrefix, StringComparison.Ordinal) && Sto.StorePackage.PlanOpen(p.Journal.LoadPlan(m.Plan, call)))
            {
                // The placed spots are not counted until they stand: one shortfall at a time.
                return null;
            }
        }
        var room = ShelterRules.StandInRoom(plan, Reviewer.RoomSelection.Get(review.Snapshot), r => RoundsBuildingPlanner.PlannedRoomStartedOf(facts, plan, r) == true);
        if (room is null)
        {
            return null;
        }
        var method = new MethodId(ShelterRules.StandInMethod(count, missing));
        var planner = Building.Copy();
        planner.Phase = Pol.Phase.HousingShelter;
        planner.Definition = SleepingRules.SleepingSpotDefinition;
        planner.Environment = Pol.PlacementEnvironment.Anywhere;
        if (!planner.MethodOnce(goal, method, call, facts.Facts.CurrentConstruction) || !RoundsBuildingPlanner.DefinitionsGate(facts, [planner.Definition]).IsZero)
        {
            return null;
        }
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        var blocked = new HashSet<Cell>(p.Journal.PendingBuildingAnchors(snapshot, [.. goal.OpenMethods.Select(m => m.Plan)], call));
        foreach (var h in p.Journal.BuildingReservations(snapshot, call))
        {
            blocked.UnionWith(h.Footprint);
        }
        if (!RoundsBuildingPlanner.DefinitionRow(facts, planner.Definition).Def.Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }
        var cells = ShelterRules.StandInAnchors(plan, room, new Cell(size.Width, size.Height)).Where(c => !c.Footprint.Any(blocked.Contains)).ToList();
        if (cells.Count == 0)
        {
            return null;
        }
        var actions = new List<DomainAction>(cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            var spot = new Domain.Building(planner.Definition, cells[i].Anchor, Rotation.South, "");
            actions.Add(DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), spot, ConstructionTierRules.PlannerTier(ShelterFamily.Sleeping.FilingKey, Pol.Phase.HousingShelter)));
        }
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("stand-in spots: p.session.State() != state");
            }
        }
        Check();
        IReadOnlyList<BuildingPreview> previews;
        if (Native is FacadeNativeClient batch)
        {
            previews = await batch.PreviewBuildingsAsync(actions, snapshot, call).ConfigureAwait(false);
        }
        else
        {
            var each = new List<BuildingPreview>(actions.Count);
            foreach (var action in actions)
            {
                each.Add(await Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false));
            }
            previews = each;
        }
        if (previews.Count != actions.Count)
        {
            throw new ControlException("stand-in spots: len(previews) != len(actions)");
        }
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        // Each spot is previewed alone, so two whose footprints overlap both preview placeable: keep the picks apart.
        var picks = ShelterRules.PickStandIns([.. cells.Select(c => c.Footprint)], blocked, i =>
            previews[i].Preview.CanPlace && previews[i].Preview.SafeToPlace, missing);
        foreach (var i in picks)
        {
            stock = RoundsBuildingPlanner.MergeRoundsStock(stock, previews[i].Stock);
            selected.Add(previews[i].Preview);
        }
        if (selected.Count == 0)
        {
            return null;
        }
        Check();
        var admitted = await planner.AdmitPreviewsAsync(new RoundsAdmission
        {
            State = state,
            Review = review,
            Owner = goal,
            Facts = facts,
            Method = method,
            Reason = string.Create(CultureInfo.InvariantCulture, $"stand-in sleeping spots for {missing} of {count}"),
            Snapshot = snapshot,
            Selected = selected,
            Stock = stock,
            Purpose = Pol.Purpose.Rounds,
        }, call, epoch).ConfigureAwait(false);
        // A refused shortfall leaves the bed ladder to go on; the next review tries the spots again.
        return admitted.Verdict == BuildingReasonAdmitted ? admitted : null;
    }
}
