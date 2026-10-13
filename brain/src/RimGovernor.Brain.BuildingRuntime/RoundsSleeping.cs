namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads a building planner needs: the colony reads plus the native placement preview.</summary>
public interface IRoundsBuildingSource : Observation.IColonySource
{
    (BuildingPreview Preview, BridgeResult Result) PreviewBuilding(CancellationToken ct, DomainAction action, GenerationSnapshot snapshot);
}

public sealed record RoundsBuildingResult
{
    public Verdict Verdict { get; init; }

    public Sto.BuildingMethodDecision Decision { get; init; } = default!;

    /// <summary>
    /// Bounds ordinary roofing or comfort use after observed construction. It neither asserts recovery nor grants
    /// native authority.
    /// </summary>
    public uint NativeWorkTicks { get; init; }
}

/// <summary>
/// Compiles one bounded building method under an existing reviewed player direction. It creates shared pending
/// work, never acquires a lease, dispatches an action or advances the game.
/// </summary>
public sealed partial class RoundsBuildingPlanner
{
    internal IReadOnlyList<Pol.SiteBuilding> Paste { get; set; } = [];

    /// <summary>The plants a refused shelter slot names on its footprint or interaction cell; the step commits their cut wave and the slot is retried.</summary>
    internal IReadOnlyList<Pol.ClearanceTarget> SlotCuts { get; set; } = [];

    internal Rounder Reviewer { get; set; } = null!;

    internal IRoundsBuildingSource Native { get; set; } = null!;

    internal ConcernId Concern { get; set; }

    /// <summary>MaintainEquipment's stand-in planner: the spot goes in the planned workshop and leaves when a weapon bench stands.</summary>
    internal bool CraftingSpot { get; set; }

    internal string Definition { get; set; } = "";

    internal double RecreationPowerW { get; set; }

    internal string Stuff { get; set; } = "";

    internal Pol.PlacementEnvironment? Environment { get; set; }

    internal IReadOnlyList<Cell> Adjacent { get; set; } = [];

    internal bool Shelter { get; set; }

    internal IRoundsExcavationSource? Excavation { get; set; }

    internal Pol.PowerProposal? Power { get; set; }

    internal Pol.TemperatureProposal? Temperature { get; set; }

    internal Pol.RefrigerationProposal? Refrigeration { get; set; }

    internal Pol.LightingProposal? Lighting { get; set; }

    internal Pol.FlooringProposal? Flooring { get; set; }

    /// <summary>Supplies MaintainFlooring's firebreak tier: the ring's pave cells still natural ground with no floor ordered.</summary>
    internal Func<IReadOnlyList<Cell>>? FirebreakPave { get; set; }

    internal Pol.RoutesProposal? Routes { get; set; }

    /// <summary>
    /// Restricts furnishing to rooms whose native role can host the function; with none observed, furnishing has
    /// no verified space and the same planner falls back to staging a starter shell for it.
    /// </summary>
    internal Pol.FacilityRequirement? Facility { get; set; }

    /// <summary>Further restricts furnishing to these observed cells (the sleeping planner's warm hosting rooms); null imposes no restriction.</summary>
    internal IReadOnlyList<Cell>? Cells { get; set; }

    /// <summary>Holds the MaintainResource pre-observation reads (deficit resource, bench census, recipe catalog) for this step only.</summary>
    internal WorkshopSelection? Workshop { get; set; }

    /// <summary>Holds the housing bedroom choice this step builds for.</summary>
    internal Pol.SleepingChoice? Sleeping { get; set; }

    /// <summary>The step of a phased goal (MaintainHousing, EnsureComfort) this planner serves; the review's latched phase decides which one runs. Empty for every other goal.</summary>
    internal Pol.Phase Phase { get; set; }

    /// <summary>
    /// Marks the research step that builds the high-tech bench and then its analyzer in the planned laboratory
    /// (never the shelter's research slot, which holds only the simple bench): whichever of the two is buildable
    /// and not yet standing is the one placed.
    /// </summary>
    internal bool AdvancedLab { get; set; }

    /// <summary>The shared room the PartySpot step places in; empty places in any roofed indoor cell.</summary>
    internal string PartySpotRoom { get; set; } = "";

    /// <summary>A copy for a derived planner (Go's <c>spot := *r</c>); the proposals it holds stay shared, as the Go pointers do.</summary>
    internal RoundsBuildingPlanner Copy() => (RoundsBuildingPlanner)MemberwiseClone();

    /// <summary>Is also used by the scheduler already holding the same player gate.</summary>
    internal RoundsBuildingResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        // The Go step defers the planner-step recording to the function's end.
        IDisposable? scope = null;
        try
        {
            return StepBody(call, epoch, arbiter, recorded => scope = recorded);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private RoundsBuildingResult StepBody(CancellationToken call, CancellationToken epoch, StepArbiter arbiter, Action<IDisposable> hold)
    {
        var r = this;
        var roofingOnly = false;
        if (r.Shelter)
        {
            // The initial shelter and expansion furnish verified indoor space first; only without any do they raise the planned shelter room.
            var indoor = r.Copy();
            indoor.Shelter = false;
            indoor.Definition = "SleepingSpot";
            var result = indoor.Step(call, epoch, arbiter);
            if (!result.Verdict.Is(Pol.Cause.NoSpace) && !result.Verdict.Is(Pol.Cause.MethodUsed) && !result.Verdict.Is(Pol.Cause.BunksOpen))
            {
                return result;
            }
            roofingOnly = result.Verdict.Is(Pol.Cause.MethodUsed);
        }
        var p = r.Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(call, review, r.Concern);
        if (!workable)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }
        // A phased goal walks its phases in order: only the planner for the phase the review left owed runs.
        if (r.Phase == ComfortRanked && review.Latches.Phase(r.Concern) == ComfortSpot)
        {
            // The PartySpot step is the ranked planner's third phase.
            var spot = r.Copy();
            spot.Phase = ComfortSpot;
            spot.Definition = PartySpotDefinition;
            spot.Environment = Pol.PlacementEnvironment.Indoors;
            r = spot;
        }
        else if (!r.Phase.IsEmpty && review.Latches.Phase(r.Concern) != r.Phase)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }
        var bunksOpen = false;
        var ringOpen = false;
        foreach (var m in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, m.Plan);
            if (r.CraftingSpot)
            {
                // Bills share the goal and run for days: only a pending spot is the planner's own work.
                foreach (var progress in plan.Progress)
                {
                    if (PendingFacility(progress, CraftingSpotDefinition))
                    {
                        return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
                    }
                }
                continue;
            }
            if (r.ButcherFacility())
            {
                // Fields, foraging and hunts share the goal and run for days: only a pending bench is the planner's own work (the ring is its own wave).
                foreach (var progress in plan.Progress)
                {
                    if (PendingFacility(progress, "TableButcher") || PendingFacility(progress, ButcherSpotDefinition))
                    {
                        return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
                    }
                }
                continue;
            }
            if (r.Phase == HousingShelter && IsShelterBunkMethod(m.MethodId))
            {
                // An open bunk rung does not hold the ring: the walls and door stand on cells the bunks never take
                // (the ring is sited around them), and a stalled bed must not keep the colony outdoors. The rungs
                // and the shell each stay idempotent on their own method binding. The indoor furnishing step still
                // waits for the bunks, and says so, so the shell step runs.
                if (!r.Shelter && StorePackage.PlanOpen(plan))
                {
                    bunksOpen = true;
                }
                continue;
            }
            if (r.Shelter && r.Phase == HousingShelter && IsShellMethod(m.MethodId))
            {
                // The ring's open wave does not hold the bunk ladder: the shelter room is one continuous
                // reconcile (StepShelterRoom), which leaves the open wave alone and goes on up the rungs.
                ringOpen = ringOpen || StorePackage.PlanOpen(plan);
                continue;
            }
            if (r.Shelter && IsRoomDigMethod(m.MethodId))
            {
                // Mining is separate work from the ring: PrepareShell reads the dig's own state and the ring goes up beside it.
                continue;
            }
            if (!r.Shelter && IsShellMethod(m.MethodId))
            {
                // A planned room's open ring wave does not hold the furniture raised on its interior with it: the
                // ring and the slot are admitted together, and ReconcileRoom leaves the ring alone while its wave is open.
                continue;
            }
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (bunksOpen)
        {
            return new RoundsBuildingResult { Verdict = BuildingBunksOpen };
        }
        var (identity, _) = r.Native.Identity(call);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            // A bare control error reads as a lost writer gate in the diagnosis; name the boundary that actually failed.
            throw new ControlException(string.Create(CultureInfo.InvariantCulture, $"step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) observed tick={expected.Tick.Value} review tick={review.Tick.Value}"));
        }
        if (r.WorkshopLadder())
        {
            var (recordedCall, recorded) = RecordPlannerStep(call, r.Concern, state.Snapshot, review.Tick);
            call = recordedCall;
            hold(recorded);
            var (selection, workshopReason) = r.PrepareWorkshop(call, state, review);
            if (!workshopReason.IsZero)
            {
                return new RoundsBuildingResult { Verdict = workshopReason };
            }
            var prepared = r.Copy();
            prepared.Workshop = selection;
            r = prepared;
        }
        IReadOnlyList<string> definitions = [r.Definition];
        if (r.Concern == EnsureCooking)
        {
            definitions = ["Campfire", "NutrientPasteDispenser", "Hopper"];
        }
        if (r.ButcherFacility())
        {
            definitions = ["TableButcher", ButcherSpotDefinition];
        }
        Pol.DiningFurniture furniture = default!;
        if (r.Phase == ComfortRanked || r.Phase == ComfortBasic)
        {
            IReadOnlyList<string> joy;
            (furniture, joy) = RecreationDefinitions(call, r.Native, Boundary.Identity(state.Snapshot));
            var names = furniture.Definitions().ToList();
            if (r.Phase != ComfortBasic)
            {
                joy = [];
            }
            foreach (var name in joy)
            {
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
            definitions = names;
        }
        if (r.WorkshopLadder())
        {
            definitions = r.Workshop!.Candidates;
        }
        if (r.Concern == MaintainMedicalReserves || r.Phase == HousingSleeping || r.Concern == EnsureResearch)
        {
            // The beds and the research bench are the furniture rules', read with every catalog (ColonyProjection.Shapes).
            definitions = [];
        }
        IReadOnlyList<string> pendingConsumers = [];
        if (r.Concern == EnsureBasicPower)
        {
            // The consumers other goals' admitted plans are about to build join the census request so the budget can price their draw.
            var held = p.Journal.BuildingReservations(call, state.Snapshot);
            pendingConsumers = PendingBuildingDefinitions(held);
            definitions = [.. new[] { "Wall", "Door" }, .. PowerFamilyDefinitions(), .. pendingConsumers];
        }
        if (r.Concern == EnsureTemperatureSafety)
        {
            definitions = [.. TemperatureDefinitions, "Heater"];
        }
        if (r.Concern == MaintainRefrigeration)
        {
            definitions = ["Cooler"];
        }
        if (r.Concern == MaintainLighting)
        {
            definitions = r.LightingDefinitions();
        }
        if (r.Concern == MaintainFlooring)
        {
            definitions = r.FlooringDefinitions();
        }
        if (r.Concern == MaintainRoutes)
        {
            definitions = r.RoutesDefinitions();
        }
        if (r.Shelter)
        {
            definitions = ["Wall", "Door"];
        }
        // The read names the definitions the step may place; the initial shelter also places its bunks before the
        // ring, and their availability is judged from the same read.
        IReadOnlyList<string> observed = definitions;
        if (r.Shelter && r.Phase == HousingShelter)
        {
            observed = [.. definitions, "SleepingSpot", ShelterBedDefinition, SleepingBedrollDefinition];
        }
        if (r.Concern == EnsureCooking || r.Concern == MaintainRefrigeration || r.ButcherFacility())
        {
            // The planned kitchen, freezer or butchery is shelled first.
            observed = [.. observed, "Wall", "Door"];
        }
        if (r.Shelter)
        {
            // The door ladder proposes an Autodoor only once the read shows it available; the ring never waits on it.
            observed = [.. observed, "Autodoor"];
        }
        Observation.ColonyReading reading;
        var roundsSource = r.Native is Observation.IRoundsSource;
        var separation = (r.ButcherFacility() || r.Concern == EnsureCooking) && roundsSource;
        if (r.Phase == ComfortSpot || r.Concern == EnsureTemperatureSafety || r.FacilityLadder() || r.Concern == MaintainRefrigeration || r.Concern == MaintainLighting || r.Concern == MaintainFlooring || r.Concern == MaintainRoutes || separation)
        {
            // Cooking and butcher placements read rooms too when the source can serve them, so kitchen/butcher
            // separation protects each other's rooms; without a census nothing is protected.
            var full = r.Reviewer.ObserveRooms(call, (Observation.IRoundsSource)r.Native, expected, Unknown<IReadOnlyList<Pol.ConstructionClaim>>(), [.. observed]);
            reading = full.ColonyReading;
        }
        else if (r.Concern == EnsureBasicPower || r.Phase == ComfortBasic || r.Shelter)
        {
            // The shelter ring reconciles against the construction census, which only the rounds read carries.
            var full = r.Reviewer.ObserveOwned(call, (Observation.IRoundsSource)r.Native, expected, Unknown<IReadOnlyList<Pol.ConstructionClaim>>(), [.. observed]);
            reading = full.ColonyReading;
        }
        else
        {
            reading = r.Reviewer.ObserveColony(call, r.Native, expected, [.. observed]);
        }
        var facts = reading.Projection;
        RecordStepRead("building", r.Concern, state.Snapshot, facts);
        RecordRoleMismatches(call, facts);
        if (r.Phase == ComfortSpot)
        {
            var (spotResult, placing) = r.PartySpotStep(call, epoch, state, review, goal, reading);
            if (placing is null)
            {
                return spotResult;
            }
            r = placing;
        }
        if (r.Concern == MaintainRoutes || r.Concern == EnsureTemperatureSafety)
        {
            var (doorResult, handled) = r.ControlRoomDoor(call, epoch, state, goal, facts);
            if (handled)
            {
                return doorResult;
            }
        }
        if (r.Concern == EnsureCooking)
        {
            // A cooking campfire a stove kitchen supersedes is deconstructed first.
            var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
            if (claims.TryGetValue(out var known))
            {
                var (campfire, owed) = CampfireRetirement(facts, known);
                if (owed)
                {
                    var retired = r.RetireCampfire(call, epoch, state, review, goal, reading, campfire);
                    if (!retired.Verdict.Is(Pol.Cause.MethodUsed))
                    {
                        return retired;
                    }
                }
            }
            var (resolvedPaste, pasteReason) = r.SelectPaste(facts);
            if (!pasteReason.IsZero)
            {
                return new RoundsBuildingResult { Verdict = pasteReason };
            }
            r = resolvedPaste;
        }
        uint coolingAllowance = 0;
        var coolingLent = false;
        if (r.FacilityLadder() || r.Phase == ComfortBasic || r.Concern == EnsureBasicPower || r.Concern == EnsureTemperatureSafety || r.Concern == MaintainRefrigeration || r.Concern == MaintainLighting || r.Concern == MaintainFlooring || r.Concern == MaintainRoutes)
        {
            RoundsBuildingPlanner? resolved;
            Verdict reason;
            if (r.Concern == EnsureTemperatureSafety)
            {
                (resolved, reason) = r.SelectTemperature(facts, review.Latches);
            }
            else if (r.Concern == MaintainRefrigeration)
            {
                bool exhausted;
                (coolingAllowance, exhausted, coolingLent) = RefrigerationOutputAllowance(call, p.Journal, goal, state.Snapshot, facts.Identity.Tick, review.Latches.RefrigerationSince);
                // The dining room's meal closet stands before its cooler; a shell already tried this epoch, or
                // refused, leaves the cooling to go on.
                var (closet, closetOwed) = PlannedMealCloset(facts);
                if (PositiveFact(closetOwed))
                {
                    var closetResult = r.ReconcileRoom(call, epoch, state, review, goal, new Observation.RoundsReading { ColonyReading = reading }, null, new RoomReconcile { RingOnly = true, Room = closet, Name = PlannedRoomMethod(closet).Value, Reason = "cold meal shelf" });
                    if (!closetResult.Verdict.SkipsToPlacement())
                    {
                        return closetResult;
                    }
                }
                (resolved, reason) = r.SelectRefrigeration(call, facts, review.Latches, exhausted);
            }
            else if (r.Concern == EnsureBasicPower)
            {
                (resolved, reason) = r.SelectPower(facts, pendingConsumers);
            }
            else if (r.Concern == MaintainLighting)
            {
                (resolved, reason) = r.SelectLighting(facts, review.Latches);
            }
            else if (r.Concern == MaintainFlooring)
            {
                (resolved, reason) = r.SelectFlooring(call, state.Snapshot, facts, review.Latches);
            }
            else if (r.Concern == MaintainRoutes)
            {
                (resolved, reason) = r.SelectRoutes(facts, review.Latches);
            }
            else if (r.WorkshopLadder())
            {
                (resolved, reason) = r.SelectWorkshop(call, state, review, facts);
            }
            else if (r.Concern == MaintainMedicalReserves)
            {
                (resolved, reason) = r.SelectHospital(facts);
            }
            else if (r.Concern == EnsureResearch)
            {
                (resolved, reason) = r.SelectResearchBench(facts);
            }
            else if (r.Phase == HousingSleeping)
            {
                (resolved, reason) = r.SelectSleeping(facts, review);
            }
            else if (r.Phase == ComfortBasic)
            {
                (resolved, reason) = r.SelectBasicComfort(facts);
            }
            else
            {
                (resolved, reason) = r.SelectComfort(facts, review.Comfort);
            }
            if (!reason.IsZero)
            {
                var result = new RoundsBuildingResult { Verdict = reason };
                if (r.Concern == EnsureBasicPower && reason.Is(Pol.Cause.NoSpace))
                {
                    return result with { NativeWorkTicks = PowerOutputAllowance(call, p.Journal, goal, state.Snapshot, facts.Identity.Tick) };
                }
                if (r.Concern == EnsureBasicPower && reason == AwaitingMethod(Pol.PowerMethod.WaitFuel))
                {
                    // An empty generator is refuelled by a native haul the census cannot see coming; like a stock
                    // refusal, the hold lends the window that haul needs rather than parking the clock on no_work.
                    return result with { NativeWorkTicks = StockWaitTicks };
                }
                // A cooler completed on the tick the supervisor latched the window reads powerOn=false until the
                // power net ticks once, so the cooling allowance also covers cooler_power_needed; a cooler still
                // unpowered when it runs out is a real hold. Time lent from the latch alone does not cover it:
                // with no method in the epoch there is no cooler still settling.
                var powerSettling = r.Concern == MaintainRefrigeration && reason == AwaitingMethod(Pol.RefrigerationMethod.PowerNeeded) && !coolingLent;
                if (reason == BuildingComfortWait || reason == AwaitingMethod(Pol.PowerMethod.WaitOutput) || reason == BuildingTemperatureWait || reason == AwaitingMethod(Pol.RefrigerationMethod.Wait) || powerSettling)
                {
                    if (r.Concern == EnsureTemperatureSafety)
                    {
                        result = result with { NativeWorkTicks = TemperatureOutputAllowance(call, facts.Shapes.Furniture.Heater, p.Journal, goal, state.Snapshot, facts.Identity.Tick) };
                    }
                    else if (r.Concern == MaintainRefrigeration)
                    {
                        result = result with { NativeWorkTicks = coolingAllowance };
                    }
                    else if (r.Concern == EnsureBasicPower)
                    {
                        result = result with { NativeWorkTicks = PowerOutputAllowance(call, p.Journal, goal, state.Snapshot, facts.Identity.Tick) };
                    }
                    else
                    {
                        result = result with { NativeWorkTicks = ComfortUseAllowance(call, p.Journal, goal, state.Snapshot, facts.Identity.Tick, furniture) };
                    }
                    p.Current(call, epoch);
                    if (p.Session.State() != state)
                    {
                        throw new ControlException("step: p.session.State() != state");
                    }
                    var (last, _) = r.Native.Identity(call);
                    Observation.Identity actual;
                    try
                    {
                        actual = ObservationPackage.DecodeIdentity(last);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        throw new ControlException("step: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)", e);
                    }
                    if (!RoundsBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick))
                    {
                        throw new ControlException("step: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)");
                    }
                    var now = r.Reviewer.Clock.Now();
                    if (now < reading.StartedAt || now - reading.StartedAt > r.Reviewer.MaxAge)
                    {
                        throw new Observation.StaleException();
                    }
                }
                return result;
            }
            r = resolved!;
            definitions = [r.Definition];
            var (furnish, furnishResult, done) = r.PlannedFacilityFurnishing(call, epoch, state, review, goal, reading, facts);
            if (done)
            {
                return furnishResult;
            }
            r = furnish!;
        }
        if (r.Temperature is not null && r.Temperature.Thing != "")
        {
            return r.CommitCampfireRefuel(call, goal, () =>
            {
                p.Current(call, epoch);
                if (p.Session.State() != state)
                {
                    throw new ControlException("step: p.session.State() != state");
                }
            });
        }
        if (r.Refrigeration is { Method: Pol.RefrigerationMethod.SetTarget })
        {
            var setResult = r.CommitRefrigerationTarget(call, goal, () =>
            {
                p.Current(call, epoch);
                if (p.Session.State() != state)
                {
                    throw new ControlException("step: p.session.State() != state");
                }
            });
            // A setpoint patch that landed on a paused game leaves the census reading the old target until the
            // game ticks (the review re-reads on tick advance), so the same patch is proposed again and found
            // used. Lend the cooling allowance anyway: the clock then runs, the census refreshes and the room cools.
            if (setResult.Verdict.Is(Pol.Cause.MethodUsed))
            {
                setResult = setResult with { NativeWorkTicks = coolingAllowance };
            }
            return setResult;
        }
        var (missing, method, selectionReason) = r.Selection(facts);
        switch (method.Value)
        {
            case "butcher-spot-retire":
            {
                var (spot, owed) = StandingButcherSpot(facts);
                if (owed)
                {
                    return r.RetireBuilding(call, epoch, state, review, goal, reading, spot.Id, spot.Building.Definition, spot.Cells[0], "butcher-spot-retire", "stand-in_butcher_spot");
                }
                break;
            }
            case "crafting-spot-retire":
            {
                var (spot, owed) = StandingCraftingSpot(facts);
                if (owed)
                {
                    return r.RetireBuilding(call, epoch, state, review, goal, reading, spot.Id, spot.Building.Definition, spot.Cells[0], "crafting-spot-retire", "stand-in_crafting_spot");
                }
                break;
            }
            case "butcher-spot":
            {
                // The free spot stands in the planned butchery until the table is built.
                var spot = r.Copy();
                spot.Definition = ButcherSpotDefinition;
                spot.Stuff = "";
                r = spot;
                break;
            }
            case "butcher-table":
            {
                // The real table goes in the planned butchery once the wood is in stock.
                var table = r.Copy();
                table.Definition = "TableButcher";
                table.Stuff = facts.BuildStuff(table.Definition);
                r = table;
                break;
            }
        }
        if (selectionReason == BuildingExistingFacility)
        {
            // The ring and the furniture are independent: a campfire already standing for the kitchen does not
            // excuse the planned room's ring.
            var (module, moduleOk) = r.PlannedRoomModule();
            if (moduleOk)
            {
                var (room, owed) = PlannedRoomOwed(facts, module);
                if (owed)
                {
                    var ring = r.ReconcileRoom(call, epoch, state, review, goal, new Observation.RoundsReading { ColonyReading = reading }, null, new RoomReconcile { RingOnly = true, Room = room, Name = PlannedRoomMethod(room).Value });
                    if (ring.Verdict.Is(Pol.Cause.MethodUsed))
                    {
                        return ring;
                    }
                }
            }
        }
        if (!selectionReason.IsZero)
        {
            return new RoundsBuildingResult { Verdict = selectionReason };
        }
        // The shelter's template slots are keyed on its planned interior, so the slot is previewed and admitted
        // first and the ring wave goes in right after it, around the placed slot: a ring blueprint on the slot's
        // interaction spot makes the native refuse the slot.
        RoomReconcile? ringAfter = null;
        if (r.TakesShelterSlot(facts))
        {
            var (shelterRoom, shelterOwed) = PlannedRoomOwed(facts, PlannedRole.Shelter);
            if (shelterOwed)
            {
                ringAfter = new RoomReconcile { RingOnly = true, Room = shelterRoom, Name = PlannedRoomMethod(shelterRoom).Value };
            }
        }
        var planner = r;
        RoundsBuildingResult AdmitRing(Sto.StandardState owner) =>
            planner.ReconcileRoom(call, epoch, state, review, owner, new Observation.RoundsReading { ColonyReading = reading }, null, ringAfter!);
        var (plannedModule, plannedModuleOk) = r.PlannedRoomModule();
        if (plannedModuleOk)
        {
            // The planned room is raised and furnished together: the ring is admitted, and the stove or cooler
            // goes onto the room's interior without waiting for the walls. A shell already tried this epoch, or
            // refused, leaves the usual placement to go on.
            var (room, owed) = PlannedRoomOwed(facts, plannedModule);
            if (owed)
            {
                var ring = r.ReconcileRoom(call, epoch, state, review, goal, new Observation.RoundsReading { ColonyReading = reading }, null, new RoomReconcile { RingOnly = true, Room = room, Name = PlannedRoomMethod(room).Value });
                if (!ring.Verdict.SkipsToPlacement())
                {
                    return ring;
                }
                if (r.HostsInPlannedRoom(plannedModule, facts))
                {
                    var kitchen = r.Copy();
                    kitchen.Cells = PlannedRoomInterior(room);
                    kitchen.Environment = Pol.PlacementEnvironment.Anywhere;
                    r = kitchen;
                }
            }
            else if (r.HostsInPlannedRoom(plannedModule, facts))
            {
                var cells = PlannedRoomCells(facts, plannedModule);
                if (cells is not null)
                {
                    var kitchen = r.Copy();
                    kitchen.Cells = cells;
                    r = kitchen;
                }
            }
        }
        if (r.CraftingSpot && r.Cells is null && PlannedShelterRooms(facts).Count == 0)
        {
            // With no shelter planned the spot never falls back to a free search: it stands in the planned
            // workshop, built or not, and waits for the plan to hold one.
            var cells = PlannedRoomCells(facts, PlannedRole.Workshop) ?? PlannedRoomPlanCells(facts, PlannedRole.Workshop);
            if (cells is null)
            {
                return new RoundsBuildingResult { Verdict = AwaitingPlan("workshop_room", "") };
            }
            var spot = r.Copy();
            spot.Cells = cells;
            spot.Environment = Pol.PlacementEnvironment.Anywhere;
            r = spot;
        }
        if (r.ButcherFacility() && r.Cells is null)
        {
            // The bench never falls back to a free search: it stands in the planned butchery, built or not, and
            // waits for the plan to hold one.
            var cells = PlannedRoomCells(facts, PlannedRole.Butchery) ?? PlannedRoomPlanCells(facts, PlannedRole.Butchery);
            if (cells is null)
            {
                return new RoundsBuildingResult { Verdict = AwaitingPlan("butchery_room", "") };
            }
            var bench = r.Copy();
            bench.Cells = cells;
            bench.Environment = Pol.PlacementEnvironment.Anywhere;
            r = bench;
        }
        if (r.Concern == EnsureCooking && r.Definition == "Campfire" && r.Cells is null && r.Paste.Count == 0 && !r.TakesShelterSlot(facts))
        {
            // The cooking campfire goes in the planned kitchen once it stands.
            var cells = PlannedRoomCells(facts, PlannedRole.Kitchen);
            if (cells is not null)
            {
                var fire = r.Copy();
                fire.Cells = cells;
                r = fire;
            }
        }
        if (r.ButcherFacility())
        {
            definitions = [r.Definition];
        }
        if (r.Concern == EnsureCooking)
        {
            var list = new List<string> { r.Definition };
            if (r.Paste.Count > 0)
            {
                list.Add("Hopper");
            }
            definitions = list;
        }
        var gate = DefinitionsGate(facts, definitions, r.Shelter);
        if (r.Paste.Count > 0)
        {
            gate = default;
            foreach (var name in definitions)
            {
                gate = ComfortBuilderGate(facts, name);
                if (!gate.IsZero)
                {
                    break;
                }
            }
        }
        if (r.Definition == "TableButcher" || r.FacilityLadder() || r.Phase == ComfortBasic || r.Phase == ComfortSpot || r.Concern == EnsureBasicPower || r.Concern == EnsureTemperatureSafety || r.Concern == MaintainRefrigeration || r.Concern == MaintainLighting || r.Concern == MaintainFlooring || r.Concern == MaintainRoutes)
        {
            gate = ComfortBuilderGate(facts, r.Definition);
            if (r.Power is { Method: Pol.PowerMethod.Generate } generator)
            {
                // A generator no site accepts yields to the next ranked one a builder here can raise.
                var alternatives = new List<string>();
                foreach (var name in generator.Alternatives)
                {
                    if (ComfortBuilderAvailable(facts, name))
                    {
                        alternatives.Add(name);
                    }
                }
                r.Power = generator with { Alternatives = alternatives };
            }
        }
        if (!gate.IsZero)
        {
            return new RoundsBuildingResult { Verdict = gate };
        }
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("step: p.session.State() != state");
            }
        }
        if (r.Shelter)
        {
            var site = new ShelterSite { State = state, Review = review, Owner = goal, Facts = facts, Read = reading, Check = Check };
            var shelterResult = r.StepShelterRoom(call, epoch, site, roofingOnly);
            if (ringOpen && shelterResult.Verdict != BuildingReasonAdmitted)
            {
                // An open ring wave is existing work unless a rung was admitted.
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
            }
            return shelterResult;
        }
        // A campfire the pawns let burn out leaves the cooking census empty again in the same epoch; the completed
        // method yields to a numbered successor the same way a staged bed's does. Indoor shelter spots do too once
        // the census shows fewer regular beds than the colony needs: a spot converted to medical (which native
        // indoor capacity excludes) or lost must not leave a spent method holding the shelter gate failed. Spots
        // that stand but are not yet counted (an open roof) keep the method used.
        if (r.Phase == HousingSleeping || r.Concern == EnsureCooking || (r.Phase == HousingShelter && RegularBedsShort(facts.Facts)))
        {
            method = r.NextSleepingBedMethod(call, goal, method, facts.Facts.CurrentConstruction);
        }
        var methodUsed = false;
        try
        {
            p.Journal.LoadOwnerMethod(call, goal, method);
            methodUsed = true;
        }
        catch (Sto.NotFoundException)
        {
        }
        if (methodUsed)
        {
            if (ringAfter is not null)
            {
                // The slot is placed already: the ring goes up around it.
                var again = AdmitRing(goal);
                if (!again.Verdict.SkipsToPlacement())
                {
                    return again;
                }
            }
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "sleeping_method") };
        }
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var heldReservations = p.Journal.BuildingReservations(call, snapshot);
        if (r.ButcherFacility() || r.Concern == EnsureCooking || r.FacilityLadder() || r.Phase == ComfortBasic || r.Phase == ComfortSpot || r.Phase == HousingExpansion || r.Concern == EnsureBasicPower || r.Concern == EnsureTemperatureSafety || r.Concern == MaintainRefrigeration || r.Concern == MaintainLighting || r.Concern == MaintainFlooring || r.Concern == MaintainRoutes)
        {
            var pendingPlanner = r;
            bool Pending(Progress progress)
            {
                if (pendingPlanner.Concern == EnsureTemperatureSafety)
                {
                    if (pendingPlanner.Temperature!.Method == Pol.TemperatureMethod.Heat || pendingPlanner.Temperature.Method == Pol.TemperatureMethod.HeatElectric)
                    {
                        return PendingFacility(progress, "Campfire") || PendingFacility(progress, "Heater");
                    }
                    return PendingFacility(progress, "PassiveCooler") || PendingFacility(progress, "Cooler");
                }
                if (pendingPlanner.Concern == EnsureBasicPower)
                {
                    foreach (var name in PowerFamilyDefinitions())
                    {
                        if (PendingFacility(progress, name))
                        {
                            return true;
                        }
                    }
                    return PendingFacility(progress, "PowerConduit") || PendingFacility(progress, "HiddenConduit") || PendingFacility(progress, "WaterproofConduit");
                }
                if (pendingPlanner.Phase == HousingExpansion || pendingPlanner.Concern == MaintainMedicalReserves || pendingPlanner.Phase == HousingSleeping)
                {
                    return PendingFacility(progress, "SleepingSpot") || PendingFacility(progress, "Bed") || PendingFacility(progress, "DoubleBed") || PendingFacility(progress, "RoyalBed");
                }
                return PendingFacility(progress, pendingPlanner.Definition);
            }
            var playerPlan = p.Journal.LoadPlan(call, state.Snapshot.Plan);
            foreach (var progress in playerPlan.Progress)
            {
                if (Pending(progress))
                {
                    return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
                }
            }
            foreach (var reservation in heldReservations)
            {
                if (Pending(reservation.Progress))
                {
                    return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
                }
            }
        }
        var protectedCells = new List<Cell>();
        foreach (var h in heldReservations)
        {
            var v = h.Progress.View;
            var hasEffect = v.Effect.TryGetValue(out var effect);
            if (!v.Unresolved && hasEffect && (effect == Effect.Completed || effect == Effect.Unsuccessful) && v.Tick.Value <= facts.Identity.Tick.Value)
            {
                continue;
            }
            protectedCells.AddRange(h.Footprint);
        }
        // Building intents admitted on other goals' plans but not yet applied show nothing on the map; keep off their anchors.
        var own = new List<PlanId>();
        foreach (var m in goal.Methods)
        {
            own.Add(m.Plan);
        }
        var pendingAnchors = p.Journal.PendingBuildingAnchors(call, snapshot, own);
        protectedCells.AddRange(pendingAnchors);
        var excavation = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts, Read = reading };
        if (r.Refrigeration is { Method: Pol.RefrigerationMethod.Build })
        {
            var (exhaust, exhausted) = r.DigExhaust(call, epoch, excavation, Check);
            if (exhausted)
            {
                return exhaust;
            }
        }
        var (sky, skyHandled) = r.DigSky(call, epoch, excavation, protectedCells, Check);
        if (skyHandled)
        {
            return sky;
        }
        if (r.Power is not null && r.Power.FixedSite())
        {
            var (geothermal, geothermalHandled) = r.DigGeothermal(call, epoch, excavation, protectedCells, Check);
            if (geothermalHandled)
            {
                return geothermal;
            }
        }
        if (r.Paste.Count > 0)
        {
            var (pasteDig, pasteHandled) = r.DigPaste(call, epoch, excavation, protectedCells, Check);
            if (pasteHandled)
            {
                return pasteDig;
            }
        }
        var (selected, stock, previewReason) = r.PreviewMethod(call, snapshot, facts, protectedCells, missing, Check);
        if (r.Routes is not null && previewReason.Is(Pol.Cause.NoSpace))
        {
            var (breach, breachHandled) = r.DigBreach(call, epoch, excavation, protectedCells, Check);
            if (breachHandled)
            {
                return breach;
            }
        }
        if (!previewReason.IsZero && r.SlotCuts.Count > 0)
        {
            // A plant on the only accepted slot is a foreign obstruction: cut it, and the slot is retried once the
            // order is done. A wave already committed keeps the wait.
            var (cut, cutDone) = r.CommitObstructionWave(call, epoch, state, goal, Wire.Name(r.Concern) + "-slot", Pol.OpKind.Cut, r.SlotCuts);
            if (cutDone)
            {
                return cut;
            }
        }
        if (!previewReason.IsZero)
        {
            return new RoundsBuildingResult { Verdict = previewReason };
        }
        // Admission never checks stock: RimWorld places the blueprints regardless and the frames hold natively for
        // materials, which MaintainResource then reads as the deficit.
        var purpose = Pol.Purpose.Rounds;
        if (r.Power is { Method: Pol.PowerMethod.Shelter })
        {
            purpose = Pol.Purpose.Shelter;
        }
        var admitted = r.AdmitPreviews(call, epoch, new RoundsAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Snapshot = snapshot, Selected = selected!, Stock = stock, Purpose = purpose });
        if (ringAfter is null || admitted.Verdict != BuildingReasonAdmitted)
        {
            return admitted;
        }
        // The slot is admitted: the shelter's ring wave goes in around it. The slot's admission moved the owner
        // on, so the ring commits against the fresh owner; a ring that is not ready now is tried again next step.
        var (owner, ownerWorkable) = p.Journal.Workable(call, review, r.Concern);
        if (!ownerWorkable)
        {
            return admitted;
        }
        AdmitRing(owner);
        return admitted;
    }
}

/// <summary>What AdmitPreviews commits: the previews a method selected under one observation, bound to the goal as that method.</summary>
internal sealed record RoundsAdmission
{
    public ControlState State { get; init; }
    public Sto.Rounds Review { get; init; } = null!;
    public Sto.StandardState Owner { get; init; } = null!;
    public Observation.ColonyProjection Facts { get; init; } = null!;
    public MethodId Method { get; init; }

    /// <summary>The planner's short why, stored with the method.</summary>
    public string Reason { get; init; } = "";
    public GenerationSnapshot Snapshot { get; init; }
    public IReadOnlyList<Pol.Preview> Selected { get; init; } = [];
    public Pol.StockObservation Stock { get; init; } = null!;
    public Pol.Purpose Purpose { get; init; }
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Admits the plan, the previews in dispatch order. Native validates each building intent when it applies it,
    /// so the siting reads carry no boundary re-check here.
    /// </summary>
    internal RoundsBuildingResult AdmitPreviews(CancellationToken call, CancellationToken epoch, RoundsAdmission a)
    {
        var p = Reviewer.Player;
        var actions = new List<DomainAction>(a.Selected.Count + 1);
        // A shell goes out as one wave with its door first in dispatch order (the preview lists it first). The
        // walls are not gated on the door completing: a door blueprint or frame no more seals a room than a
        // wall's does, and gating held every wall until the door stood, or for ever when its observation came
        // back unknown. The pen shell in rounds_animal_containment.go orders its ring the same way.
        foreach (var v in a.Selected)
        {
            actions.Add(v.Action);
        }
        var (roof, roofed) = PorchRoofAction(a, actions.Count);
        if (roofed)
        {
            actions.Add(roof);
        }
        var plan = new PlanSpec(a.Snapshot.Plan, a.Snapshot.Revision, actions);
        var decision = AdmitMethod(call, p.Journal, new Sto.BuildingMethodRequest
        {
            Owner = a.Owner,
            Method = a.Method,
            Reason = a.Reason,
            Plan = plan,
            Current = a.Snapshot,
            Tick = a.Facts.Identity.Tick,
            Bounds = Known<Pol.Rectangle>(a.Facts.Bounds),
            Stock = a.Stock,
            Previews = a.Selected,
            Purpose = a.Purpose,
        });
        var reason = AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
            // A shell admitted short of a material records each shortfall so the construction demand
            // MaintainResource supplies covers it. The review keeps a record only while its actions stay open.
            // A Project owner (the planned kitchen's shell under EnsureCooking) has no Standard: the record
            // names Episodes, so it records none.
            if (a.Purpose == Pol.Purpose.Shelter && decision.Standard.Standard.Id.Length != 0)
            {
                foreach (var resource in PreviewResources(a.Selected))
                {
                    var (rec, isShort) = StorePackage.ShortfallDependency(Concern, decision.Standard.Standard, a.Method, plan.Id, a.Selected, a.Stock, resource, a.Facts.Identity.Tick);
                    if (!isShort)
                    {
                        continue;
                    }
                    try
                    {
                        p.Journal.RecordDependency(call, a.Review.Revision, rec);
                    }
                    catch (Sto.ConflictException)
                    {
                    }
                }
            }
        }
        return new RoundsBuildingResult { Verdict = reason, Decision = decision };
    }

    internal (IReadOnlyList<Pol.Preview>? Previews, Pol.StockObservation Stock, Verdict Verdict) PreviewMethod(CancellationToken call, GenerationSnapshot snapshot, Observation.ColonyProjection facts, IReadOnlyList<Cell> protectedCells, long missing, Action check)
    {
        if (Power is { Method: Pol.PowerMethod.Shelter })
        {
            return PreviewPowerShelter(call, snapshot, facts, protectedCells, check);
        }
        if (Power is { Method: Pol.PowerMethod.Connect })
        {
            return PreviewPowerRoute(call, snapshot, facts, protectedCells, check);
        }
        if (Power is not null && Power.FixedSite())
        {
            return PreviewPowerSite(call, snapshot, facts, protectedCells, check);
        }
        var (selected, stock, reason) = PreviewPowerOrSearch(call, snapshot, facts, protectedCells, missing, check);
        if (Power is { Method: Pol.PowerMethod.Generate })
        {
            // A generator with no acceptable site near the consumer (a wind turbine whose every catch zone is
            // obstructed) yields to the next ranked definition under the same method.
            foreach (var name in Power.Alternatives)
            {
                if (!reason.Is(Pol.Cause.NoSpace))
                {
                    break;
                }
                var next = Copy();
                next.Definition = name;
                (selected, stock, reason) = next.PreviewPowerOrSearch(call, snapshot, facts, protectedCells, missing, check);
            }
        }
        return (selected, stock, reason);
    }

    internal (IReadOnlyList<Pol.Preview>? Previews, Pol.StockObservation Stock, Verdict Verdict) PreviewSearch(CancellationToken call, GenerationSnapshot snapshot, Observation.ColonyProjection facts, IReadOnlyList<Cell> protectedCells, long missing, Action check)
    {
        if (Paste.Count > 0)
        {
            return PreviewPaste(call, snapshot, facts, protectedCells, check);
        }
        if (Refrigeration is not null)
        {
            return PreviewRefrigeration(call, snapshot, facts, protectedCells, check);
        }
        if (Temperature is { Method: Pol.TemperatureMethod.CoolPowered })
        {
            return PreviewWallBuilding(call, snapshot, facts, protectedCells, check, Wire.Name(Pol.TemperatureMethod.CoolPowered), Temperature.Cell, Temperature.Rotation, false);
        }
        if (Lighting is not null)
        {
            return PreviewLighting(call, snapshot, facts, protectedCells, check);
        }
        if (Flooring is not null)
        {
            return PreviewFlooring(call, snapshot, facts, protectedCells, check);
        }
        if (Routes is not null)
        {
            return PreviewRoutes(call, snapshot, facts, protectedCells, check);
        }
        // A loose sleeping spot stands only in a room planned to sleep in; the basic-comfort table and seat keep
        // out of the rooms that must stay clear.
        var looseSpot = Concern == MaintainHousing && Definition == "SleepingSpot" && Facility is null && Cells is null;
        IReadOnlyList<Cell> protectedSet = protectedCells;
        if (!looseSpot && (Concern == MaintainHousing || (Concern == EnsureComfort && (Phase == ComfortBasic || Phase == ComfortSpot) && Cells is null)) && Facility is null)
        {
            // Loose sleeping spots and the basic-comfort table and seat go to the starter shell or a bedroom, not
            // into a standing kitchen, lab, storage or other planned room: beds there make it a sleeping room and
            // block the room's own furnishing, and furniture there carves up its warehouse zone.
            protectedSet = [.. protectedSet, .. NonSleepingPlannedCells(facts)];
        }
        if (looseSpot)
        {
            // A loose spot off the template's bed slots must not take the shelter's campfire, cooler, crafting or
            // bench slot: a cold map's cooking campfire then finds its slot taken and falls through to the kitchen.
            protectedSet = [.. protectedSet, .. ShelterFurnitureCells(facts)];
        }
        if (ButcherBenchDefinition(Definition) || Concern == EnsureCooking)
        {
            protectedSet = [.. protectedSet, .. SeparationProtectedCells(facts.Rooms, ButcherBenchDefinition(Definition))];
        }
        // The cooking campfire stands in the planned kitchen; on a cold map it takes the shelter's template slot.
        // It has no outdoor stand-in.
        var cookingCampfire = Concern == EnsureCooking && Definition == "Campfire" && Facility is null && Paste.Count == 0;
        var slotProtected = protectedSet;
        var cells = new List<Pol.SiteCell>();
        var adjacent = new HashSet<Cell>(Adjacent);
        var roomCells = new HashSet<Cell>();
        var interiorRooms = new List<Pol.InteriorRoom>();
        var restricted = Temperature is not null || Facility is not null || Cells is not null;
        if (looseSpot)
        {
            restricted = true;
            foreach (var c in SleepingPlannedCells(facts))
            {
                roomCells.Add(c);
            }
        }
        // The crafting spot takes the standing shelter's template slot and moves to the planned workshop only once the shelter has left the plan.
        var slotOnly = CraftingSpot && Facility is null && PlannedShelterRooms(facts).Count > 0;
        // On a cold map the campfires stand indoors on the shelter's template slots; a taken slot, or no shelter,
        // leaves the campfire to the search below.
        if (facts.LayoutPlan.TryGetValue(out var coldPlan) && coldPlan.Cold && Definition == "Campfire" && (cookingCampfire || (Temperature is { Method: Pol.TemperatureMethod.Heat })))
        {
            slotOnly = PlannedShelterRooms(facts).Count > 0;
        }
        // A hot map's passive cooler for the shelter stands on the template's cooler slot; the powered wall cooler path is unchanged.
        if (facts.LayoutPlan.TryGetValue(out var hotPlan) && hotPlan.Hot && Definition == "PassiveCooler" && Temperature is { Method: Pol.TemperatureMethod.Cool })
        {
            foreach (var shelter in PlannedShelterRooms(facts))
            {
                foreach (var c in Temperature.Cells)
                {
                    var interior = shelter.Interior;
                    slotOnly = slotOnly || (c.X >= interior.X && c.X < interior.X + interior.Width && c.Z >= interior.Z && c.Z < interior.Z + interior.Height);
                }
            }
        }
        if (cookingCampfire && Cells is null && !slotOnly)
        {
            // Cooking waits for the planned kitchen.
            return (null, new Pol.StockObservation(), BuildingNoLayoutPlan);
        }
        // The slots come from the planned interior, not a standing census room, so they place before any wall does.
        var plannedInterior = slotOnly;
        if (slotOnly)
        {
            interiorRooms = [.. PlannedShelterRooms(facts)];
        }
        // A campfire or cooler for a planned shelter has no outdoor stand-in: a slot the native refuses waits,
        // named by its blocker, instead of the search below placing one outside the room.
        var strictSlot = slotOnly && interiorRooms.Count > 0;
        RefusedPlacement? refusedSlot = null;
        Pol.Preview? lastPreview = null;
        if (Temperature is not null)
        {
            foreach (var c in Temperature.Cells)
            {
                roomCells.Add(c);
            }
        }
        if (Facility is not null)
        {
            var planned = ResearchHostRooms(facts);
            if (Facility.Role == Pol.RoomRole.Laboratory && !AdvancedLab && planned.Count > 0)
            {
                // The research bench takes the planned shelter's bench row.
                interiorRooms = [.. planned];
                plannedInterior = true;
                foreach (var room in planned)
                {
                    foreach (var c in RectCells(room.Interior))
                    {
                        roomCells.Add(c);
                    }
                }
            }
            else if (Cells is not null)
            {
                // A bed's cells are the planned bedroom's; its interior slots come from the plan room they lie in.
                foreach (var c in Cells)
                {
                    roomCells.Add(c);
                }
                var own = Cells;
                interiorRooms = [.. PlannedInteriorRooms(facts, plannedRoom =>
                {
                    var inside = plannedRoom.Region.Bounds();
                    return own.Any(c => c.X >= inside.X && c.X < inside.X + inside.Width && c.Z >= inside.Z && c.Z < inside.Z + inside.Height);
                })];
            }
            // Every other facility furnishes through its planned room, which sets Cells (PlannedFacilityFurnishing);
            // with none planned there is no room to place in.
            if (roomCells.Count == 0)
            {
                return (null, new Pol.StockObservation(), NoSpace("hosting_room"));
            }
        }
        if (Cells is not null)
        {
            var allowed = new HashSet<Cell>();
            foreach (var c in Cells)
            {
                allowed.Add(c);
                if (Facility is null)
                {
                    // No facility: the cells are the room (the kitchen).
                    roomCells.Add(c);
                }
            }
            roomCells.RemoveWhere(c => !allowed.Contains(c));
            if (roomCells.Count == 0)
            {
                return (null, new Pol.StockObservation(), NoSpace("layout_room_cells"));
            }
        }
        foreach (var c in facts.Cells)
        {
            if (RecreationPowerW > 0 && !PoweredRecreationCell(facts, c.Cell, RecreationPowerW))
            {
                continue;
            }
            if (restricted && !roomCells.Contains(c.Cell))
            {
                continue;
            }
            if (ComfortFurniture(facts).IsChair(Definition) && !adjacent.Contains(c.Cell))
            {
                continue;
            }
            if (Environment == Pol.PlacementEnvironment.Anywhere || (c.Roofed.TryGetValue(out var roofed) && roofed))
            {
                cells.Add(c);
            }
        }
        var planned0 = facts.Center().TryGetValue(out var center);
        if (!planned0 && Cells is null && Power is null)
        {
            return (null, new Pol.StockObservation(), BuildingNoLayoutPlan);
        }
        var searchRequest = new Pol.PlacementSearchRequest
        {
            Snapshot = snapshot,
            Tick = facts.Identity.Tick,
            Bounds = facts.Bounds,
            Center = center,
            Cells = cells,
            Protected = [.. protectedSet, .. DoorwayAisles(facts.Bounds, facts.Cells)],
            Environment = Pol.PlacementEnvironment.Indoors,
            Radius = Math.Max(facts.Bounds.Width, facts.Bounds.Height),
            Limit = 64,
            StockpileZones = StockpileZoneIds(facts),
        };
        if (Facility is not null)
        {
            // A facility furnishes the free room of its role nearest the colony centre; the search spans the map,
            // so the starter shell stays a candidate until such a room stands.
            var anchor = center;
            var (module, moduleOk) = RoomModule();
            if (moduleOk)
            {
                var (near, nearOk) = RoomAnchor(facts, module, center);
                if (nearOk)
                {
                    anchor = near;
                }
            }
            searchRequest = searchRequest with { Center = anchor };
        }
        if ((Concern == EnsureCooking && Definition == "Campfire") || ButcherBenchDefinition(Definition))
        {
            // The cooking campfire and the butcher table stand by the base, not the landing centroid.
            var (anchor, anchorOk) = PlanCore(facts);
            if (anchorOk)
            {
                searchRequest = searchRequest with { Center = anchor };
            }
        }
        if (Cells is not null)
        {
            // The room is fixed: search around it, wherever it stands.
            var box = CellsBox(roomCells);
            searchRequest = searchRequest with
            {
                Center = new Cell(box.X + box.Width / 2, box.Z + box.Height / 2),
                Radius = Math.Max(box.Width, box.Height) / 2 + 1,
            };
        }
        if (Power is not null)
        {
            searchRequest = searchRequest with { Center = Power.Center, Radius = 6 };
            if (Definition == WindTurbineDefinition)
            {
                // A turbine wants open ground its 7x16 catch zone can lie on, which the cells right beside a consumer rarely offer.
                searchRequest = searchRequest with { Radius = 12 };
            }
        }
        if (Environment is { } environment)
        {
            searchRequest = searchRequest with { Environment = environment };
        }
        var search = NewPlacementSearch(searchRequest);
        var selected = new List<Pol.Preview>();
        var unknownWatch = false;
        var watchBuildings = facts.Facts.Comfort.TryGetValue(out var comfort) ? comfort.WatchBuildings : [];
        var usedCells = new HashSet<Cell>();
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        // A workshop bench has an interaction spot on one side, so a small room often rejects the north-facing
        // anchor ("interaction spot is blocked by wooden wall") while another facing fits; every other definition
        // keeps the single native-default orientation.
        Rotation[] rotations = Workshop is not null ? [Rotation.North, Rotation.East, Rotation.South, Rotation.West] : [Rotation.North];
        PlacementChoice? pending = null;
        void Commit()
        {
            selected.Add(pending!.Choice);
            stock = MergeRoundsStock(stock, pending.Preview.Stock, selected.Count == 1);
            if (pending.Choice.Footprint.TryGetValue(out var footprint))
            {
                foreach (var c in footprint)
                {
                    usedCells.Add(c);
                }
            }
            pending = null;
        }
        // PreviewAt previews the definition at one anchor and rotation; not ok when the native preview or the
        // search refuses the site.
        (PlacementChoice? Choice, bool Ok, Verdict Reason) PreviewAt(Pol.PlacementSearch siteSearch, string id, Cell c, Rotation rotation)
        {
            check();
            var b = new Building(Definition, c, rotation, Stuff);
            var a = DomainAction.NewBuildingAction(new ActionId(id), b, PlannerTier(Concern, Phase));
            var (preview, _) = Native.PreviewBuilding(call, a, snapshot);
            lastPreview = preview.Preview;
            var madeKnown = preview.Preview.MadeFromStuff.TryGetValue(out var made);
            if ((Phase == ComfortRanked || Phase == ComfortBasic) && watchBuildings.Contains(Definition))
            {
                var accessibleKnown = preview.Preview.WatchCellsAccessible.TryGetValue(out var accessible);
                unknownWatch = unknownWatch || !accessibleKnown;
                if (!accessibleKnown || !accessible)
                {
                    return (null, false, default);
                }
            }
            if (!madeKnown)
            {
                return (null, false, FieldUnavailable("made_from_stuff"));
            }
            if (made != (Stuff != ""))
            {
                return (null, false, SiteBlocked(Definition, "stuff_differs_from_native"));
            }
            if (Definition == WindTurbineDefinition)
            {
                // Only a site whose native catch zone is clear makes the turbine's nominal output; an obstructed one is no site.
                if (!preview.Preview.WindBlockedCells.TryGetValue(out var blocked) || blocked > 0)
                {
                    return (null, false, default);
                }
            }
            var (choice, score, ok) = siteSearch.SelectScored(Definition, Stuff, [preview.Preview]);
            if (!ok)
            {
                return (null, false, default);
            }
            return (new PlacementChoice { Choice = choice, Preview = preview, Score = score }, true, default);
        }
        var occupied = new List<Cell>();
        if (interiorRooms.Count > 0)
        {
            foreach (var c in facts.Cells)
            {
                if (c.Occupied())
                {
                    occupied.Add(c.Cell);
                }
            }
        }
        // Overlaps refuses a footprint on a site already chosen, or one that cuts a planned room's door-to-door aisle or strands its floor.
        bool Overlaps(Pol.Preview candidate)
        {
            candidate.Footprint.TryGetValue(out var footprint);
            footprint ??= [];
            foreach (var c in footprint)
            {
                if (usedCells.Contains(c))
                {
                    return true;
                }
            }
            foreach (var room in interiorRooms)
            {
                var blocked = new HashSet<Cell>(occupied);
                blocked.UnionWith(usedCells);
                if (!InteriorPlacementWalkable(room, blocked, footprint))
                {
                    return true;
                }
            }
            return false;
        }
        // Pass runs one search. Candidates come nearest first, and a footprint's score is its distance, so once the
        // next candidate's distance reaches the best valid score no later candidate can beat it: the best is
        // committed and the previews stop there.
        Verdict Pass(Pol.PlacementSearch passSearch, string tag)
        {
            var index = 0;
            foreach (var c in passSearch.Candidates())
            {
                var i = index++;
                if (selected.Count == missing)
                {
                    break;
                }
                if (pending is not null && passSearch.Score(c, null).Distance >= pending.Score.Score)
                {
                    Commit();
                    if (selected.Count == missing)
                    {
                        break;
                    }
                }
                foreach (var rotation in rotations)
                {
                    var id = string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}{tag}-{i}");
                    if (rotation != Rotation.North)
                    {
                        id = string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}{tag}-{i}-{Wire.Name(rotation)}");
                    }
                    var (choice, ok, reason) = PreviewAt(passSearch, id, c, rotation);
                    if (!reason.IsZero)
                    {
                        return reason;
                    }
                    if (!ok)
                    {
                        continue;
                    }
                    if (!Overlaps(choice!.Choice) && (pending is null || choice.Score.Score < pending.Score.Score))
                    {
                        pending = choice;
                    }
                    break;
                }
            }
            if (pending is not null && selected.Count < missing)
            {
                Commit();
            }
            return default;
        }
        // An indoor facility asks each room's interior template for the piece's slots first and takes any slot
        // that can be placed, whatever its score. Only then does the scored search run, over the rooms' wall
        // bands, centre lines and mirror positions before any other free cell.
        if (interiorRooms.Count > 0)
        {
            var slots = new List<Pol.InteriorPiece>();
            var anchors = new List<Cell>();
            foreach (var room in interiorRooms)
            {
                anchors.AddRange(InteriorSnapAnchors(room, occupied));
                // A definition with no buildable row has no shape or family: the template plans its default layout for it.
                var (piece, _) = room.Piece(Definition);
                piece = piece with { Def = Definition };
                var (roomPlan, planOk) = PlanRoomInterior(room, piece);
                if (!planOk)
                {
                    continue;
                }
                foreach (var pc in roomPlan.Pieces)
                {
                    // The search drops occupied anchors, so a slot whose anchor is taken cannot be previewed.
                    if (pc.Accepts(room.Shapes, Definition) && !occupied.Contains(pc.Anchor()))
                    {
                        slots.Add(pc);
                    }
                }
            }
            if (slots.Count > 0)
            {
                var slotAnchors = slots.Select(s => s.Anchor()).ToList();
                var request = searchRequest with
                {
                    Protected = [.. slotProtected, .. DoorwayAisles(facts.Bounds, facts.Cells)],
                    Anchors = slotAnchors,
                    Limit = Math.Min(slots.Count, 64),
                };
                if (plannedInterior)
                {
                    // The slots are keyed on the planned footprint, walled or not: the interior's cells stand in whatever their roof.
                    request = request with { Environment = Pol.PlacementEnvironment.Anywhere, Cells = PlannedInteriorSiteCells(facts, interiorRooms) };
                }
                var slotSearch = NewPlacementSearch(request);
                // The search drops anchors that are protected, occupied or off the site cells; previewing one anyway fails the whole step.
                var proposed = new HashSet<Cell>(slotSearch.Candidates());
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (selected.Count == missing)
                    {
                        break;
                    }
                    if (!proposed.Contains(slot.Anchor()))
                    {
                        continue;
                    }
                    var (choice, ok, reason) = PreviewAt(slotSearch, string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-t{i}"), slot.Anchor(), slot.Rot);
                    if (!reason.IsZero)
                    {
                        return (null, new Pol.StockObservation(), reason);
                    }
                    if (!ok && strictSlot && refusedSlot is null)
                    {
                        var shown = lastPreview ?? new Pol.Preview();
                        refusedSlot = ReportRefused(call, new RoomBuild { Def = Definition, Cell = slot.Anchor() }, shown);
                        SlotCuts = SlotPlantCuts(facts.Cells, slot, shown.Blockers);
                    }
                    if (ok && !Overlaps(choice!.Choice))
                    {
                        pending = choice;
                        Commit();
                    }
                }
            }
            if (selected.Count < missing && !slotOnly)
            {
                var snapSearch = NewPlacementSearch(searchRequest with { Anchors = anchors });
                var snapReason = Pass(snapSearch, "-s");
                if (!snapReason.IsZero)
                {
                    return (null, new Pol.StockObservation(), snapReason);
                }
            }
        }
        if (selected.Count < missing && strictSlot)
        {
            var subject = "shelter_slot:none_free";
            if (refusedSlot is not null)
            {
                subject = "shelter_slot:blocked:" + refusedSlot.Key();
            }
            return (null, new Pol.StockObservation(), WaitFor(Pol.Cause.ExistingWork, subject));
        }
        if (selected.Count < missing)
        {
            var reason = Pass(search, "");
            if (!reason.IsZero)
            {
                return (null, new Pol.StockObservation(), reason);
            }
        }
        if (selected.Count != missing)
        {
            if (unknownWatch)
            {
                return (null, new Pol.StockObservation(), FieldUnavailable("watch_cells_accessible"));
            }
            return (null, new Pol.StockObservation(), NoSpace("placement_site"));
        }
        return (selected, stock, default);
    }

    /// <summary>
    /// Returns the method the next build takes. The selection names a method by the beds still owed, but that
    /// count need not fall after a staged bed is assigned (the colonist it went to may have been counted as
    /// housed, or another colonist's bed may have turned unsuitable), so a method whose plan is no longer open
    /// yields to a numbered successor; only a method whose plan is still open stays the one reported used. A
    /// building gone from the census (a campfire that burnt out, a bed that disappeared) and one never built both
    /// mean try again.
    /// </summary>
    internal MethodId NextSleepingBedMethod(CancellationToken call, Sto.StandardState goal, MethodId method, Fact<Pol.CurrentConstruction> census)
    {
        var p = Reviewer.Player;
        var baseMethod = method;
        for (var n = 1; n < SleepingBedsPerEpoch; n++)
        {
            Sto.MethodState existing;
            try
            {
                existing = p.Journal.LoadOwnerMethod(call, goal, method);
            }
            catch (Sto.NotFoundException)
            {
                return method;
            }
            var plan = p.Journal.LoadPlan(call, existing.Plan);
            if (StorePackage.PlanWorkOpen(plan, census))
            {
                return method;
            }
            method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{baseMethod.Value}-{n}"));
        }
        return method;
    }
}

/// <summary>A valid previewed footprint held while nearer-scored candidates are still possible.</summary>
internal sealed class PlacementChoice
{
    public Pol.Preview Choice { get; init; } = null!;
    public BuildingPreview Preview { get; init; } = null!;
    public Pol.PlacementScore Score { get; init; } = null!;
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The scope every routine planner checks its own identity read against: the reviewed world and native
    /// generation, at a tick that still describes the review's anchor (RoundsBuildingFresh).
    /// </summary>
    internal static bool RoundsBuildingBoundary(Observation.Identity actual, GenerationSnapshot expected, Tick tick) =>
        actual.NativeGeneration.TryGetValue(out var generation)
        && actual.Colony == expected.Colony && actual.Load == expected.Load && actual.Map == expected.Map
        && RoundsBuildingFresh(actual.Tick, tick) && generation == expected.Native;

    /// <summary>
    /// Accepts an identity read the step's fact cache may serve: seeded at the tick the step opened on, it sits
    /// behind the review's colony anchor under a running window and is not a changed world.
    /// </summary>
    internal static bool RoundsBuildingFresh(Tick actual, Tick anchor) => RoundsCachedFresh(FactFamily.Identity, actual, anchor);

    /// <summary>
    /// Accepts any observation the step's fact cache may have served: a read's tick never makes it stale, only
    /// another world does, and that is checked beside it.
    /// </summary>
    internal static bool RoundsCachedFresh(FactFamily family, Tick actual, Tick anchor) => true;

    /// <summary>One clock window lent to a method refused for insufficient stock. The census counts what lies on the map, and a stack a pawn is carrying to a stockpile, a bench is about to finish or a hauler is about to unforbid is invisible to it; without ticks the haul never lands and the refusal repeats until the clock parks on no_work. The next step re-reads the census, so the wait is the window, not a belief about stock.</summary>
    internal const uint StockWaitTicks = GameTime.TicksPerHour;

    /// <summary>Bounds the builds one epoch may stage under the same selection: beds for one owed count, campfires after burn-outs.</summary>
    internal const int SleepingBedsPerEpoch = 8;

    /// <summary>The distinct resources the previews' known costs name, sorted.</summary>
    internal static List<Pol.Resource> PreviewResources(IReadOnlyList<Pol.Preview> previews)
    {
        var seen = new HashSet<Pol.Resource>();
        var output = new List<Pol.Resource>();
        foreach (var p in previews)
        {
            if (!p.Costs.TryGetValue(out var costs))
            {
                continue;
            }
            foreach (var c in costs)
            {
                if (c.Count > 0 && seen.Add(c.Resource))
                {
                    output.Add(c.Resource);
                }
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return output;
    }

    /// <summary>The smallest rectangle holding every cell; cells is non-empty.</summary>
    internal static Pol.Rectangle CellsBox(IReadOnlyCollection<Cell> cells)
    {
        var first = true;
        int minX = 0, minZ = 0, maxX = 0, maxZ = 0;
        foreach (var c in cells)
        {
            if (first)
            {
                minX = maxX = c.X;
                minZ = maxZ = c.Z;
                first = false;
            }
            minX = Math.Min(minX, c.X);
            minZ = Math.Min(minZ, c.Z);
            maxX = Math.Max(maxX, c.X);
            maxZ = Math.Max(maxZ, c.Z);
        }
        return new Pol.Rectangle { X = minX, Z = minZ, Width = maxX - minX + 1, Height = maxZ - minZ + 1 };
    }

    /// <summary>
    /// Whether the sleeping census holds fewer regular (humanlike, non-medical, non-prisoner) beds than
    /// max(colonists, housing target) plus the colony's slaves, whose beds count as regular: the shelter's spots
    /// were lost or converted, not merely uncounted.
    /// </summary>
    internal static bool RegularBedsShort(Pol.RoundsFacts f)
    {
        var known = f.Sleeping.TryGetValue(out var sleeping);
        var ck = f.Colonists.TryGetValue(out var count);
        if (!known || !ck)
        {
            return false;
        }
        if (f.HousingTarget.TryGetValue(out var target))
        {
            count = Math.Max(count, target);
        }
        count += sleeping!.Slaves.Count;
        long regular = 0;
        foreach (var bed in sleeping.Beds)
        {
            var hk = bed.Humanlike.TryGetValue(out var humanlike);
            var mk = bed.Medical.TryGetValue(out var medical);
            var pk = bed.Prisoners.TryGetValue(out var prisoners);
            if (hk && mk && pk && humanlike && !medical && !prisoners)
            {
                regular++;
            }
        }
        return regular < count;
    }

    /// <summary>
    /// The interior cells of the layout plan's rooms built for another use than sleeping. Only the shelter (the
    /// starter room) and unbuilt reserve ground stay open to loose spots and furniture.
    /// </summary>
    internal static List<Cell> NonSleepingPlannedCells(Observation.ColonyProjection facts)
    {
        var cells = new List<Cell>();
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return cells;
        }
        foreach (var room in plan.Rooms)
        {
            switch (room.Role)
            {
                case Pol.PlannedRole.Kitchen:
                case Pol.PlannedRole.Lab:
                case Pol.PlannedRole.Hospital:
                case Pol.PlannedRole.Prison:
                case Pol.PlannedRole.Dining:
                case Pol.PlannedRole.Rec:
                case Pol.PlannedRole.Freezer:
                case Pol.PlannedRole.Workshop:
                case Pol.PlannedRole.MealCloset:
                case Pol.PlannedRole.Butchery:
                case Pol.PlannedRole.Throne:
                case Pol.PlannedRole.Nursery:
                case Pol.PlannedRole.Playroom:
                case Pol.PlannedRole.Classroom:
                case Pol.PlannedRole.DeathrestChamber:
                case Pol.PlannedRole.Worship:
                case Pol.PlannedRole.ContainmentCell:
                case Pol.PlannedRole.IsolationRoom:
                case Pol.PlannedRole.Storage:
                case Pol.PlannedRole.Armory:
                case Pol.PlannedRole.Wardrobe:
                case Pol.PlannedRole.Barn:
                    cells.AddRange(PlannedRoomInterior(room));
                    break;
            }
        }
        return cells;
    }

    /// <summary>The cells of the planned shelter template's pieces that are not bunks: its campfire, cooler, crafting spot and research bench.</summary>
    internal static List<Cell> ShelterFurnitureCells(Observation.ColonyProjection facts)
    {
        var cells = new List<Cell>();
        foreach (var room in PlannedShelterRooms(facts))
        {
            var (full, ok) = PlanRoomInterior(room, new Pol.InteriorPieceDef());
            if (!ok)
            {
                continue;
            }
            foreach (var p in full.Pieces)
            {
                if (!p.IsBunk())
                {
                    cells.AddRange(RectCells(p.Rect));
                }
            }
        }
        return cells;
    }

    /// <summary>The interior cells of the layout plan's rooms a colonist sleeps in: the shelter, the bedrooms and the suites. Loose sleeping spots stand in these and nowhere else.</summary>
    internal static List<Cell> SleepingPlannedCells(Observation.ColonyProjection facts)
    {
        var cells = new List<Cell>();
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return cells;
        }
        foreach (var room in plan.Rooms)
        {
            if (room.Role is Pol.PlannedRole.Shelter or Pol.PlannedRole.Bedroom or Pol.PlannedRole.Suite)
            {
                cells.AddRange(PlannedRoomInterior(room));
            }
        }
        return cells;
    }
}
