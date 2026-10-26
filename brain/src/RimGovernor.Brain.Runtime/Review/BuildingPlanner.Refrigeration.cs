using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    internal const uint RefrigerationCoolingTicks = 2 * GameTime.TicksPerDay;

    /// <summary>The definitions the refrigeration step reads: the cooler, and the wall and door of the cold rooms it shells.</summary>
    internal IReadOnlyList<string> RefrigerationDefinitions() => [Definition, ContainmentCellRules.ShellWallDefinition, ContainmentCellRules.ShellDoorDefinition];

    /// <summary>The shared building planner under the food-stores/refrigeration Need (#3280): its Methods bind under the Need and are admitted by claims.</summary>
    public static BuildingPlanner NewReviewRefrigerationPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewRefrigerationPlanner: reviewer or native missing");
        }

        if (native is not IReviewSource || native is not IRefrigerationSource)
        {
            throw new ControlException("NewReviewRefrigerationPlanner: native lacks the rounds or refrigeration source");
        }

        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = FoodStoresRules.RefrigerationNeed.FilingKey, Need = FoodStoresRules.RefrigerationNeed, Definition = "Cooler" };
    }

    internal async Task<(BuildingPlanner? Planner, Verdict Verdict)> SelectRefrigerationAsync(ColonyReading facts, bool allowance, CancellationToken ct)
    {
        var review = RefrigerationRules.ReviewRefrigeration(facts.Facts.FoodStorageUpkeep, Reviewer.Policy.FoodStorage);
        review = review.WithWarmRooms(BurialFacts.WarmCoolingRooms(facts));
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        var observed = await RefrigerationReads.RefrigerationFacts((IRefrigerationSource)Native, facts, Definition, ct).ConfigureAwait(false);
        var proposal = RefrigerationRules.SelectRefrigerationMethod(review, observed, Reviewer.Policy.FoodStorage, allowance);
        switch (proposal.Method)
        {
            case RefrigerationMethod.Build:
            case RefrigerationMethod.SetTarget:
                var resolved = Copy();
                resolved.Refrigeration = proposal;
                resolved.Definition = "Cooler";
                return (resolved, default);

            case RefrigerationMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case RefrigerationMethod.None:
            case RefrigerationMethod.EnclosureNeeded:
            case RefrigerationMethod.ResearchNeeded:
            case RefrigerationMethod.PowerNeeded:
            case RefrigerationMethod.HeatRejectionBlocked:
            case RefrigerationMethod.NoWall:
            case RefrigerationMethod.Wait:
            case RefrigerationMethod.WaitBlackout:
                return (null, AwaitingMethod(proposal.Method));
        }
        throw new DomainException($"undefined refrigeration method {(int)proposal.Method}");
    }

    /// <summary>
    /// The refrigeration Need's work step (#3280), run by the food stores family for the ready Need under its owner.
    /// It reads the rooms, shells the owed meal closet, picks the cooler method, and commits the setpoint, or the
    /// freezer's ring and the cooler itself. No whole-owner guard holds it: each target is
    /// committed once under the Need while its work is open or what it built stands (a destroyed cooler or ring is
    /// committed again, decision 19), and an overlapping footprint is held by the claim.
    /// </summary>
    internal async Task<Verdict> RefrigerationStepAsync(NeedWork work, CancellationToken call)
    {
        var p = Reviewer.Player;
        var state = work.State;
        var epoch = work.Epoch;
        NeedOwner goal = work.Owner;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return BuildingReasonNoReview;
        }
        var identity = await Native.IdentityAsync(call).ConfigureAwait(false);
        var expected = new ObservationIdentity { Context = identity.Context, Paused = identity.Paused };
        if (!BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException(string.Create(CultureInfo.InvariantCulture, $"refrigeration step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) observed tick={expected.Tick.Value} review tick={review.Tick.Value}"));
        }
        var full = await Reviewer.ObserveOwnedAsync((IReviewSource)Native, expected, RefrigerationDefinitions(), call).ConfigureAwait(false);
        var reading = full.Reading;
        var facts = reading;
        PlannerSnapshot.RecordStepRead("building", Concern, state.Snapshot, facts);
        RecordRoleMismatches(facts, call);
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("refrigeration step: p.session.State() != state");
            }
        }
        var rounds = new ReviewReading { Reading = reading, Frame = new BridgeReviewFrame(), Emergency = new EmergencyFacts() };

        var (exhausted, committed) = RefrigerationCooling(p.Journal, goal, state.Snapshot, facts.Identity.Tick, call);
        // The dining room's meal closet stands before its cooler; a shell already committed, or refused, leaves the
        // cooling to go on.
        var (closet, closetOwed) = BurialFacts.PlannedMealCloset(facts);
        if (closetOwed)
        {
            var closetResult = await ReconcileRoomAsync(state, review, goal, rounds, null, new RoomReconcile { RingOnly = true, Room = closet, Name = PlannedRoomMethod(closet).Value, Reason = "cold meal shelf" }, call, epoch).ConfigureAwait(false);
            if (!closetResult.Verdict.SkipsToPlacement)
            {
                return closetResult.Verdict;
            }
        }
        var (resolved, reason) = await SelectRefrigerationAsync(facts, exhausted, call).ConfigureAwait(false);
        if (!reason.IsZero)
        {
            // A cooler completed on the tick the review saw reads powerOn=false until the power net ticks once, so
            // the cooling window also covers cooler_power_needed while the Need has a committed method; with none
            // there is no cooler still settling.
            if (reason == AwaitingMethod(RefrigerationMethod.Wait) || (reason == AwaitingMethod(RefrigerationMethod.PowerNeeded) && committed))
            {
                await WaitFenceAsync(state, reading, facts, call, epoch).ConfigureAwait(false);
            }
            return reason;
        }
        var r = resolved!;
        if (r.Refrigeration is { Method: RefrigerationMethod.SetTarget })
        {
            return r.CommitRefrigerationTarget(goal, Check, call).Verdict;
        }
        var method = r.Refrigeration!.Key;

        // The planned freezer is raised and furnished together: its ring is admitted and the cooler goes on its
        // wall without waiting for the walls. A ring already committed, or refused, leaves the cooler to go on.
        var (freezer, freezerOwed) = PlannedRoomOwed(facts, PlannedRole.Freezer);
        if (freezerOwed)
        {
            var ring = await r.ReconcileRoomAsync(state, review, goal, rounds, null, new RoomReconcile { RingOnly = true, Room = freezer, Name = PlannedRoomMethod(freezer).Value }, call, epoch).ConfigureAwait(false);
            if (!ring.Verdict.SkipsToPlacement)
            {
                return ring.Verdict;
            }
        }
        var gate = ComfortBuilderGate(facts, r.Definition);
        if (!gate.IsZero)
        {
            return gate;
        }
        if (!r.MethodOnce(goal, method, call, facts.Facts.CurrentConstruction))
        {
            return WaitFor(Cause.MethodUsed, "sleeping_method");
        }

        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var heldReservations = p.Journal.BuildingReservations(snapshot, call);
        // One cooler is pending at a time: the player's or another plan's cooler still to stand holds this one.
        var playerPlan = p.Journal.LoadPlan(state.Snapshot.Plan, call);
        if (playerPlan.Progress.Any(progress => PendingFacility(progress, r.Definition)) || heldReservations.Any(h => PendingFacility(h.Progress, r.Definition)))
        {
            return BuildingReasonExistingWork;
        }
        var protectedCells = new List<Cell>();
        foreach (var h in heldReservations)
        {
            var v = h.Progress.View;
            if (
                !v.Unresolved
                && v.Effect
                    is EffectState.Observed { Kind: Effect.Completed or Effect.Unsuccessful }
                && v.Tick.Value <= facts.Identity.Tick.Value
            )
            {
                continue;
            }
            protectedCells.AddRange(h.Footprint);
        }
        // Building intents admitted on other owners' plans but not yet applied show nothing on the map; keep off their anchors.
        protectedCells.AddRange(p.Journal.PendingBuildingAnchors(snapshot, [.. goal.OpenMethods.Select(m => m.Plan)], call));

        var (selected, stock, previewReason) = await r.PreviewRefrigerationAsync(snapshot, facts, protectedCells, Check, call).ConfigureAwait(false);
        if (!previewReason.IsZero)
        {
            return previewReason;
        }
        // Admission never checks stock: RimWorld places the blueprints regardless and the frames hold natively for
        // materials, which the economy/resource Need then reads as the deficit.
        var admitted = await r.AdmitPreviewsAsync(new ReviewAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Snapshot = snapshot, Selected = selected, Stock = stock, Purpose = Purpose.ReviewState }, call, epoch).ConfigureAwait(false);
        return admitted.Verdict;
    }

    /// <summary>
    /// Whether the cooling window of the Need's coolers has run out, and whether the Need has committed any method
    /// in its world. With none no cooler is known to be settling, so no second cooler is lent time (decision 16).
    /// </summary>
    internal static (bool Exhausted, bool Committed) RefrigerationCooling(StoreJournal journal, NeedOwner goal, GenerationSnapshot current, Tick tick, CancellationToken ct)
    {
        var methods = journal.OwnerMethodHistory(goal, ct);
        if (methods.Count == 0)
        {
            return (false, false);
        }

        var open = false;
        var exhausted = false;
        foreach (var method in methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            var (remaining, completed) = RefrigerationCoolingRemaining(plan, current, tick);
            open = open || remaining > 0;
            exhausted = exhausted || (completed && remaining == 0);
        }

        return (exhausted && !open, true);
    }

    internal static (long Remaining, bool Completed) RefrigerationCoolingRemaining(PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count != 1)
        {
            return (0, false);
        }

        var progress = plan.Progress[0];
        var action = progress.Action;
        if (action.Building is { } building)
        {
            if (building.Definition != "Cooler")
            {
                return (0, false);
            }
        }
        else if (action.BuildingTemperature is null)
        {
            return (0, false);
        }

        current = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
        var v = progress.View;
        current = current with { Native = v.Snapshot.Native };
        if (
            v.Stage != Stage.Completed
            || v.Unresolved
            || !v.Observes(Effect.Completed)
            || !v.Snapshot.Matches(current)
            || tick < v.Tick
        )
        {
            return (0, false);
        }

        if (tick.Value - v.Tick.Value >= RefrigerationCoolingTicks)
        {
            return (0, true);
        }

        return (RefrigerationCoolingTicks - (tick.Value - v.Tick.Value), true);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewRefrigerationAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        if (Refrigeration is null || Refrigeration.Method != RefrigerationMethod.Build)
        {
            throw new ControlException("previewRefrigeration: no refrigeration build proposal");
        }

        return await PreviewWallBuildingAsync(snapshot, facts, protectedCells, check, "Build", Refrigeration.Cell, Refrigeration.Rotation, false, ct).ConfigureAwait(false);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewWallBuildingAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, string def, Cell cell, Rotation rotation, bool overRock, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (protectedCells.Contains(cell))
        {
            return (null!, stock, BuildingReasonExistingWork);
        }

        var available = ColonyPlanningRules.DefinitionAvailable(facts.Facts, def);

        if (!available)
        {
            return (null!, stock, FieldUnavailable(def + "_research"));
        }

        var building = new Building(def, cell, rotation, "");
        var (previews, planned, reason) = await PreviewPlannedBuildingAsync(snapshot, facts, building, 0, overRock, false, ct).ConfigureAwait(false);
        if (!reason.IsZero)
        {
            return (null!, planned, reason);
        }

        var footprint = previews[0].Footprint;
        if (footprint.Count != 1 || footprint[0] != cell)
        {
            return (null!, planned, NoSpace(def + "_wall_footprint"));
        }

        return (previews, planned, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPlannedBuildingAsync(GenerationSnapshot snapshot, ColonyReading facts, Building building, int index, bool overRock, bool goOpen, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{index}"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
        BuildingPreview preview;
        if (overRock)
        {
            if (Native is not GameClient source)
            {
                return (null!, stock, FieldUnavailable("over_rock_preview"));
            }

            preview = await source.PreviewBuildingOverRockAsync(action, snapshot, ct).ConfigureAwait(false);
        }
        else
        {
            preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
        }

        var p = preview.Preview;
        if (goOpen && !p.CanPlace && p.Blockers.Count == 0)
        {
            throw new ControlException($"rock step: native refused {building.Definition} at {building.Cell} that the frame lists open and names no blocker");
        }

        if (p.MadeFromStuff != (building.Stuff != "") || !p.Footprint.Contains(building.Cell) || !p.CanPlace || !p.SafeToPlace)
        {
            return (null!, stock, NoSpace("refrigeration_site"));
        }

        stock = MergeReviewStock(stock, preview.Stock);
        return ([p], stock, default);
    }

    internal BuildingResult CommitRefrigerationTarget(NeedOwner goal, Action check, CancellationToken call)
    {
        var proposal = Refrigeration;
        if (proposal is null || proposal.Method != RefrigerationMethod.SetTarget)
        {
            throw new ControlException("commitRefrigerationTarget: no set-target proposal");
        }

        return CommitTemperatureTarget(goal, proposal.Key, proposal.Cooler, proposal.TargetC, check, call);
    }

    private BuildingResult CommitTemperatureTarget(NeedOwner goal, MethodId method, string building, double target, Action check, CancellationToken call)
    {
        var p = Reviewer.Player;
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "refrigeration_target") };
        }

        var patch = new BuildingTemperature(building, target);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBuildingTemperatureAction(new ActionId($"{id}-0"), patch);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        check();
        return new BuildingResult { Verdict = OwnerCommitVerdict(p.Journal.CommitOwnerWork(goal, method, "", plan, call)) };
    }
}
