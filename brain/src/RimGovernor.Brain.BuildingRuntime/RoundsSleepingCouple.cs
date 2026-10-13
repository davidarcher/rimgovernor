namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    private const string CouplePackMethod = "sleeping-couple-pack-";

    /// <summary>The cells of the beds this epoch's completed couple pack steps uninstalled, each plan's first (the couple's room's) first.</summary>
    internal List<Cell> CouplePacked(Sto.StandardState goal, CancellationToken call)
    {
        var p = Reviewer.Player;
        var methods = p.Journal.LoadOwnerMethods(goal, call);
        var output = new List<Cell>();
        foreach (var m in methods)
        {
            if (!m.MethodId.Value.StartsWith(CouplePackMethod, StringComparison.Ordinal))
            {
                continue;
            }
            var plan = p.Journal.LoadPlan(m.Plan, call);
            if (plan.Progress.Count == 0 || plan.Progress[0].View.Stage != Stage.Completed)
            {
                continue;
            }
            if (plan.Progress[0].Action.UninstallBuilding is { } u)
            {
                output.Add(u.Cell);
            }
        }
        return output;
    }

    /// <summary>
    /// The couple bed lever: pack the couple's single beds, then install a DoubleBed (a stored one first) in
    /// the couple's room's bedroom slot; each step once per Episode. due is false when nothing is to do, so the
    /// ordinary sleeping choice goes on.
    /// </summary>
    internal async Task<(RoundsBuildingResult Result, bool Due)> CoupleBedAsync(PackedStock stock, ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, CancellationToken call, CancellationToken epoch)
    {
        var none = (new RoundsBuildingResult(), false);
        var facts = reading.Projection;
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!sk || !rk || !ck || !census!.Colony)
        {
            return none;
        }
        var packed = CouplePacked(goal, call);
        facts.DefinitionAvailable(SleepingCoupleBedDefinition).TryGetValue(out var buildable);
        facts.LayoutPlan.TryGetValue(out var plan);
        if (NextCoupleBed(obs!, plan!, CoupleBedRooms(rooms!, census, facts.Cells), packed, buildable) is not { } step)
        {
            return none;
        }
        var p = Reviewer.Player;
        bool Used(MethodId method)
        {
            try
            {
                p.Journal.LoadOwnerMethod(goal, method, call);
                return true;
            }
            catch (Sto.NotFoundException)
            {
                return false;
            }
        }
        switch (step.Kind)
        {
            case Pol.CoupleBedKind.CouplePack:
                var key = "";
                foreach (var piece in step.Pack)
                {
                    key += piece.Thing + "/";
                }
                var method = new MethodId(CouplePackMethod + DigestHex(key, 8));
                if (Used(method))
                {
                    return none;
                }
                var id = PlanIds.MintPlanId();
                var actions = new List<DomainAction>(step.Pack.Count);
                for (var i = 0; i < step.Pack.Count; i++)
                {
                    var piece = step.Pack[i];
                    var value = new MoveBuilding(piece.Thing, piece.Def, AnchorForRect(piece.Rect, piece.Size, piece.Rot), piece.Rot);
                    actions.Add(DomainAction.NewUninstallBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), value));
                }
                return CommitCouple(state, goal, method, id, actions, call, epoch);
            case Pol.CoupleBedKind.CoupleInstall:
                // The couple's room is reconciled to the DoubleBed in its bed slot: a stored one is installed, else one is built.
                if (step.Planned is not { } planned)
                {
                    return none;
                }
                var inside = planned.Region.Bounds();
                var result = await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile
                {
                    Room = planned,
                    Template = step.Template,
                    Name = string.Create(CultureInfo.InvariantCulture, $"sleeping-couple-install-{inside.X}-{inside.Z}"),
                    Reason = "couple's double bed",
                }, call, epoch).ConfigureAwait(false);
                // A wave already tried this epoch, or refused, leaves the ordinary choice to go on.
                return (result, result.Verdict == BuildingReasonAdmitted);
        }
        return none;
    }

    /// <summary>
    /// Answers a SleepingBuild with a stored packed bed of the chosen definition, installed at the first free
    /// bed spot of a hosting room; once per packed bed per Episode. due is false when none is stored or none
    /// fits, so the build goes on.
    /// </summary>
    internal async Task<(RoundsBuildingResult Result, bool Due)> ReinstallStoredBedAsync(PackedStock stock, ControlState state, Sto.StandardState goal, Observation.RoundsReading reading, Pol.SleepingChoice choice, CancellationToken call, CancellationToken epoch)
    {
        var none = (new RoundsBuildingResult(), false);
        var facts = reading.Projection;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!rk || !ck || !census!.Colony)
        {
            return none;
        }
        var hosting = new HashSet<Cell>(choice.Cells);
        var p = Reviewer.Player;
        foreach (var room in FurnitureRooms(rooms!, census, facts.Cells))
        {
            if (BedSpot(room, new Pol.Resource(choice.Definition)) is not var (anchor, rot) || !hosting.Contains(anchor))
            {
                continue;
            }
            var move = await stock.InstallAsync(PackedFurnitureDefinition, choice.Definition, anchor, rot, call).ConfigureAwait(false);
            if (move is null)
            {
                return none;
            }
            var method = new MethodId("sleeping-reinstall-" + DigestHex(move.Thing, 8));
            try
            {
                p.Journal.LoadOwnerMethod(goal, method, call);
                return none;
            }
            catch (Sto.NotFoundException)
            {
            }
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewMoveBuildingAction(new ActionId($"{id}-0"), move);
            return CommitCouple(state, goal, method, id, [action], call, epoch);
        }
        return none;
    }

    internal (RoundsBuildingResult Result, bool Due) CommitCouple(ControlState state, Sto.StandardState goal, MethodId method, PlanId id, IReadOnlyList<DomainAction> actions, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("commitCouple: p.session.State() != state");
        }
        p.Journal.CommitOwnerMethod(goal, method, "", plan, call);
        return (new RoundsBuildingResult { Verdict = BuildingReasonAdmitted }, true);
    }
}
