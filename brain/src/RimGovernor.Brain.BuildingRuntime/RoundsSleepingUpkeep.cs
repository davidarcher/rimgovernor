namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Answers MaintainHousing's bedroom phase: it transfers ownership of a vacant suitable bed to a colonist
/// without one (a one-shot AssignIntent native checks for roof, access, allowed area and the pawn's
/// comfortable band), and when no bed can be assigned it stages one through the same building ladder the
/// hospital walks (furnish a Bedroom-hosting room warm enough for the waiting colonists, else a starter shell
/// first), assigning it on a later review. Neither the assignment nor the construction receipt recovers the
/// goal; ReviewSleeping does, on the assigned pawn's observed use of that bed.
/// </summary>
public sealed partial class RoundsSleepingUpkeepPlanner
{
    internal Rounder Reviewer { get; }
    internal IRoundsBuildingSource Native { get; }
    internal RoundsBuildingPlanner Building { get; }

    private RoundsSleepingUpkeepPlanner(Rounder reviewer, IRoundsBuildingSource native, RoundsBuildingPlanner building)
    {
        Reviewer = reviewer;
        Native = native;
        Building = building;
    }

    public static RoundsSleepingUpkeepPlanner Create(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsSleepingUpkeepPlanner: reviewer == nil || native == nil");
        }
        if (native is not Observation.IRoundsSource)
        {
            throw new ControlException("NewRoundsSleepingUpkeepPlanner: !ok");
        }
        var building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MaintainHousing, Phase = Pol.Phase.HousingSleeping };
        return new RoundsSleepingUpkeepPlanner(reviewer, native, building);
    }

    /// <summary>
    /// The clock window inside which a completed assignment's sleep is observed: one full day covers the next
    /// rest period, and the budget is not renewed by later observations.
    /// </summary>
    private const int SleepingObservationBudget = GameTime.TicksPerDay;

    /// <summary>One clock window inside that budget.</summary>
    private const int SleepingObservationSlice = 600;

    /// <summary>The clock window a completed assignment of this epoch still earns; only observed use recovers the goal, and without ticks nobody sleeps.</summary>
    internal static uint SleepingNativeWorkTicks(Sto.PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count != 1)
        {
            return 0;
        }
        var p = plan.Progress[0];
        if (p.Action.Assign is null)
        {
            return 0;
        }
        // The assignment's observation is taken at the root plan's scope, so only the world (colony, load, map) is compared, not the plan.
        var v = p.View;
        var known = v.Effect.TryGetValue(out var effect);
        if (v.Stage != Stage.Completed || v.Unresolved || !known || effect != Effect.Completed || !Boundary.World(v.Snapshot, current) || tick.Value < v.Tick.Value || tick.Value - v.Tick.Value >= SleepingObservationBudget)
        {
            return 0;
        }
        return Math.Min((uint)SleepingObservationSlice, (uint)(SleepingObservationBudget - (tick.Value - v.Tick.Value)));
    }

    internal async Task<RoundsBuildingResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
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
        var (goal, workable) = p.Journal.Workable(review, MaintainHousing, call);
        if (!workable || goal is null || review.Latches.Phase(MaintainHousing) != Pol.Phase.HousingSleeping)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var m in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(m.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
            }
        }
        // A completed assignment is retired from the goal's active methods once its epoch is cleaned, so the
        // epoch's full method history is what still earns the observation window.
        uint observe = 0;
        var methods = p.Journal.LoadOwnerMethods(goal, call);
        foreach (var m in methods)
        {
            // Only a bed assignment is followed by observed sleep.
            if (m.MethodId.Value.StartsWith("throne-", StringComparison.Ordinal))
            {
                continue;
            }
            var plan = p.Journal.LoadPlan(m.Plan, call);
            observe = Math.Max(observe, SleepingNativeWorkTicks(plan, state.Snapshot, review.Tick));
        }
        var result = await DecideAsync(arbiter, state, review, goal, call, epoch).ConfigureAwait(false);
        if (result.Verdict != BuildingReasonAdmitted && result.NativeWorkTicks == 0)
        {
            result = result with { NativeWorkTicks = observe };
        }
        return result;
    }

    private async Task<RoundsBuildingResult> DecideAsync(StepArbiter arbiter, ControlState state, Sto.Rounds review, Sto.StandardState goal, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var started = Reviewer.Clock.Now();
        var (identity, _) = await Native.IdentityAsync(call).ConfigureAwait(false);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("decide: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        List<string> definitions = ["Wall", "Door", .. RoomBeautyDefinitions(), .. Reviewer.RememberedThrones(), .. Reviewer.Census.RememberedWorship()];
        var reading = await Reviewer.ObserveRoomsAsync((Observation.IRoundsSource)Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), [.. definitions], call).ConfigureAwait(false);
        var facts = reading.Projection;
        // One packed read per pass, shared by every owner of stored pieces.
        var stock = new PackedStock(Native, Boundary.Identity(state.Snapshot));
        var request = SleepingRequest(facts, review);
        var choice = SelectSleepingMethod(request);
        // swapping flags a bedroom swap: native evicts the bed's owner.
        var swapping = false;
        // A couple's double bed comes before any other bed change.
        if (choice.Method != Pol.SleepingMethod.Unknown)
        {
            var (couple, due) = await CoupleBedAsync(stock, state, review, goal, reading, call, epoch).ConfigureAwait(false);
            if (due)
            {
                return couple;
            }
        }
        if (BedroomsFirst(choice.Method) && BedroomStepOf(facts, Reviewer.Stage).Kind != Pol.BedroomStepKind.None)
        {
            choice = choice with { Method = Pol.SleepingMethod.NoDemand };
        }
        switch (choice.Method)
        {
            case Pol.SleepingMethod.Unknown:
                return new RoundsBuildingResult { Verdict = FieldUnavailable(choice.Missing) };
            case Pol.SleepingMethod.NoDemand:
            {
                // A bed replacement under way finishes first: its new bed stands unowned until the owner moves.
                var (rep, repDue) = BedReplacementOf(facts, Reviewer.Stage);
                if (repDue && rep.Step == Pol.BedReplacementStep.Assign)
                {
                    choice = new Pol.SleepingChoice { Method = Pol.SleepingMethod.Assign, Pawn = rep.Pawn, Bed = rep.Bed, PreviousBed = rep.PreviousBed };
                    break;
                }
                if (repDue && rep.Step == Pol.BedReplacementStep.Remove)
                {
                    return await RemoveOldBedAsync(state, review, goal, reading, rep, call, epoch).ConfigureAwait(false);
                }
                // Everyone owns a bed: walk them into planned bedrooms.
                var step = BedroomStepOf(facts, Reviewer.Stage);
                if (step.Kind == Pol.BedroomStepKind.None)
                {
                    // Then pawns leave a Retiring wing, one per step.
                    step = MigrateStep(facts);
                }
                switch (step.Kind)
                {
                    case Pol.BedroomStepKind.Move:
                        choice = new Pol.SleepingChoice { Method = Pol.SleepingMethod.Assign, Pawn = step.Pawn, Bed = step.Bed, PreviousBed = step.PreviousBed };
                        break;
                    case Pol.BedroomStepKind.Reconcile:
                        // The room's ring, floor and bed through the shared build side; a bed left empty in the starter shell is packed and reinstalled rather than built again.
                        return await ReconcileBedroomAsync(stock, state, review, goal, reading, step, call, epoch).ConfigureAwait(false);
                    case Pol.BedroomStepKind.Clear:
                        return await RemoveOldBedAsync(state, review, goal, reading, new Pol.BedReplacement { Room = "shell", Bed = step.Bed, Def = SleepingSpotDefinition.ToString(), Cell = step.Cells[0] }, call, epoch).ConfigureAwait(false);
                    default:
                    {
                        // The title's throne room: reconciled to the plan and its template, then furnished through the room upgrade below.
                        var throne = ThroneStepOf(facts);
                        if (throne.Owed)
                        {
                            return await StageThroneAsync(arbiter, stock, state, review, goal, reading, throne, call, epoch).ConfigureAwait(false);
                        }
                        if (throne.Failed)
                        {
                            // A requirement without an available definition is a named failure; forbidden buildings are packed.
                            return new RoundsBuildingResult { Verdict = SiteBlocked("throne room", throne.Detail()) };
                        }
                        // The Biotech child rooms, worship room and containment cell: reconciled to the plan and the role's template.
                        var child = ChildRoomStep(facts);
                        if (child.Owed())
                        {
                            return await StageChildRoomAsync(stock, state, review, goal, reading, child, call, epoch).ConfigureAwait(false);
                        }
                        var (swap, ok) = BedroomSwapOf(facts);
                        if (!ok)
                        {
                            var (title, titleDue) = TitleFurniture(facts);
                            if (titleDue)
                            {
                                return await UpgradeBedroomAsync(state, review, goal, reading, title, call, epoch).ConfigureAwait(false);
                            }
                            var (companion, companionDue) = CompanionBed(facts);
                            if (companionDue)
                            {
                                return await UpgradeBedroomAsync(state, review, goal, reading, companion, call, epoch).ConfigureAwait(false);
                            }
                            var (upgrade, upgradeDue) = RoomUpgradeOf(facts, Reviewer.Stage);
                            if (upgradeDue)
                            {
                                return await UpgradeBedroomAsync(state, review, goal, reading, upgrade, call, epoch).ConfigureAwait(false);
                            }
                            var (replacement, replacementDue) = BedReplacementOf(facts, Reviewer.Stage);
                            if (replacementDue && replacement.Step == Pol.BedReplacementStep.Build)
                            {
                                return await UpgradeBedroomAsync(state, review, goal, reading, new Pol.RoomUpgrade { Room = replacement.Room, Slot = "bed", Def = replacement.Def, Stuff = replacement.Stuff, Anchor = replacement.Cell, Rot = replacement.Rot }, call, epoch).ConfigureAwait(false);
                            }
                            var (beauty, beautyDue) = BeautyUpgrade(facts, Reviewer.Stage);
                            if (beautyDue)
                            {
                                return await UpgradeBedroomAsync(state, review, goal, reading, beauty, call, epoch).ConfigureAwait(false);
                            }
                            var (sculpted, sculptDue) = await SculptBedroomAsync(stock, state, goal, reading, call, epoch).ConfigureAwait(false);
                            if (sculptDue)
                            {
                                return sculpted;
                            }
                            return new RoundsBuildingResult { Verdict = BuildingReasonSleepingUseNeeded };
                        }
                        choice = new Pol.SleepingChoice { Method = Pol.SleepingMethod.Assign, Pawn = swap.Pawn, Bed = swap.Bed, PreviousBed = swap.PreviousBed };
                        swapping = true;
                        break;
                    }
                }
                break;
            }
            case Pol.SleepingMethod.Unavailable:
                return new RoundsBuildingResult { Verdict = BuildingReasonSleepingUnavailable };
            case Pol.SleepingMethod.MarkSlaves:
                return MarkSlaveBed(state, goal, choice.Bed, call, epoch);
            case Pol.SleepingMethod.Build:
            {
                // A stored bed is reinstalled before a new one is built.
                var (reinstalled, due) = await ReinstallStoredBedAsync(stock, state, goal, reading, choice, call, epoch).ConfigureAwait(false);
                if (due)
                {
                    return reinstalled;
                }
                return await Building.StepAsync(arbiter, call, epoch).ConfigureAwait(false);
            }
        }
        // Assign: one pawn, one bed, once per Episode. A method that already ran this epoch (the native side
        // refused it, or the player undid it) is not retried; the next epoch reconsiders. The one exception is
        // an intent native refused: that leaves no effect behind, so a bounded number of fresh attempts follow.
        var (method, barred) = AssignMethod(goal, $"sleeping-assign-{choice.Pawn}-{choice.Bed}", state.Snapshot, call);
        if (!barred.IsZero)
        {
            return new RoundsBuildingResult { Verdict = barred };
        }
        if (method.Value == "")
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "bed_assignment") };
        }
        var previous = choice.PreviousBed != "" ? PreviousAssignment.KnownPrevious(choice.PreviousBed) : PreviousAssignment.ClearPrevious();
        var assign = new Domain.Assign(new Domain.PawnId(choice.Pawn.Value), choice.Bed, previous);
        if (swapping)
        {
            assign = assign.AsSwap();
        }
        if (!arbiter.TryClaim([], "bed:" + choice.Bed))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "bed_claim") };
        }
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAssignAction(new ActionId($"{id}-0"), assign);
        var spec = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("decide: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        var latest = p.Journal.LoadRounds(call);
        if (latest.Revision != review.Revision || !latest.Enabled)
        {
            throw new ControlException("decide: latest.Revision != review.Revision || !latest.Enabled");
        }
        p.Journal.CommitOwnerMethod(goal, method, "", spec, call);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }

    /// <summary>
    /// The method ID for the next assignment attempt of this epoch under base, or "" when the pair was already
    /// applied, or the shared refusal budget bars another attempt (the verdict then says why).
    /// </summary>
    internal (MethodId Method, Verdict Verdict) AssignMethod(Sto.StandardState goal, string baseName, GenerationSnapshot world, CancellationToken call)
    {
        var p = Reviewer.Player;
        var refused = new List<PlanId>();
        for (var attempt = 0; ; attempt++)
        {
            var method = new MethodId(attempt > 0 ? string.Create(CultureInfo.InvariantCulture, $"{baseName}-retry{attempt}") : baseName);
            Method existing;
            try
            {
                existing = p.Journal.LoadOwnerMethod(goal, method, call);
            }
            catch (Sto.NotFoundException)
            {
                var (verdict, ok) = AdmitSubject(p.Journal, baseName, refused, world, call);
                if (!ok)
                {
                    return (new MethodId(""), verdict);
                }
                return (method, new Verdict());
            }
            var plan = p.Journal.LoadPlan(existing.Plan, call);
            if (!SleepingAssignUnadmitted(plan.Progress))
            {
                return (new MethodId(""), new Verdict());
            }
            refused.Add(existing.Plan);
        }
    }

    /// <summary>Whether a settled plan has every action ended absent: native refused it, so nothing changed.</summary>
    internal static bool SleepingAssignUnadmitted(IReadOnlyList<Progress> progress)
    {
        if (progress.Count == 0 || Standard.WorkOpen(progress))
        {
            return false;
        }
        foreach (var pr in progress)
        {
            if (!pr.View.Effect.TryGetValue(out var effect) || effect != Effect.Absent)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Commits one patch setting bed for slaves, once per bed per Episode; the next review assigns the waiting slave to it.</summary>
    internal RoundsBuildingResult MarkSlaveBed(ControlState state, Sto.StandardState goal, string bed, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var method = new MethodId("sleeping-slave-bed-" + bed);
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "slave_bed_marking") };
        }
        catch (Sto.NotFoundException)
        {
        }
        var patch = BedUse.NewBedSlaves(bed);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBedUseAction(new ActionId($"{id}-0"), patch);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("markSlaveBed: p.session.State() != state");
        }
        p.Journal.CommitOwnerMethod(goal, method, "", plan, call);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Resolves the building ladder's definition and site from the same census the sleeping planner chose
    /// from: only a SleepingBuild choice furnishes; every other outcome is reported, never built around.
    /// </summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectSleeping(Observation.ColonyProjection facts, Sto.Rounds review)
    {
        var request = SleepingRequest(facts, review);
        var choice = SelectSleepingMethod(request);
        switch (choice.Method)
        {
            case Pol.SleepingMethod.Unknown:
                return (null, FieldUnavailable(choice.Missing));
            case Pol.SleepingMethod.NoDemand:
                return (null, BuildingReasonSleepingUseNeeded);
            case Pol.SleepingMethod.Assign:
                return (null, AwaitingPlan("bed_assignment", ""));
            case Pol.SleepingMethod.Unavailable:
                return (null, BuildingReasonSleepingUnavailable);
        }
        return ResolveSleeping(facts, choice);
    }

    /// <summary>The building ladder resolved for a SleepingBuild choice.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) ResolveSleeping(Observation.ColonyProjection facts, Pol.SleepingChoice choice)
    {
        if (choice.Cells.Count == 0)
        {
            return (null, NoSpace("sleeping_cells"));
        }
        var facility = Facility(Pol.RoomRole.Bedroom);
        var resolved = Copy();
        resolved.Definition = choice.Definition;
        resolved.Environment = Pol.PlacementEnvironment.Indoors;
        resolved.Facility = facility;
        resolved.Cells = choice.Cells;
        resolved.Sleeping = choice;
        resolved.Stuff = BedStuff(facts, resolved.Definition);
        return (resolved, new Verdict());
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Re-derives the sleeping targets from this step's census against the review's retained use history, so
    /// the choice and the build site share one observation.
    /// </summary>
    internal static Pol.SleepingRequest SleepingRequest(Observation.ColonyProjection facts, Sto.Rounds review)
    {
        var sleeping = ReviewSleeping(facts.Facts.Sleeping, review.Sleeping, facts.Identity.Tick);
        EquatableArray<Pol.BenchDefinition> definitions = [.. facts.Definitions.Select(d => new Pol.BenchDefinition { Name = d.Name, Available = d.Available, NeedsPower = d.NeedsPower, ConstructionSkill = d.ConstructionSkill })];
        var stocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { SleepingBedrollDefinition, SleepingCoupleBedrollDefinition })
        {
            if (facts.StockedStuff(name).HasValue)
            {
                stocked.Add(name);
            }
        }
        var request = new Pol.SleepingRequest
        {
            Targets = sleeping.Targets,
            Sleeping = facts.Facts.Sleeping,
            Rooms = facts.Rooms,
            Definitions = definitions,
            Furniture = facts.Shapes.Furniture,
            Stocked = stocked,
            Traits = SleepingTraits(facts),
        };
        if (facts.Facts.Sleeping.TryGetValue(out var obs))
        {
            facts.TechTier.TryGetValue(out var tier);
            request = request with { RoomTargets = RoomQualityTargets(obs, request.Traits ?? new Dictionary<Pol.PawnId, Pol.TraitEffects>(), tier, facts.Impressiveness) };
        }
        return request;
    }

    /// <summary>Each colonist's trait effects from the work census, null while it is unknown.</summary>
    internal static Dictionary<Pol.PawnId, Pol.TraitEffects>? SleepingTraits(Observation.ColonyProjection facts)
    {
        if (!facts.WorkPawns.TryGetValue(out var pawns))
        {
            return null;
        }
        var output = new Dictionary<PawnId, Pol.TraitEffects>(pawns.Count);
        foreach (var p in pawns)
        {
            output[p.Id] = BuildProfile(p).Effects;
        }
        return output;
    }

    /// <summary>The next room quality swap: a jealous colonist into the best solo bedroom, an ascetic into the plainest.</summary>
    internal static (Pol.BedroomSwap Swap, bool Ok) BedroomSwapOf(Observation.ColonyProjection facts)
    {
        var known = facts.Facts.Sleeping.TryGetValue(out var obs);
        var traits = SleepingTraits(facts);
        if (!known || traits is null)
        {
            return (new Pol.BedroomSwap(), false);
        }
        if (!facts.LayoutPlan.TryGetValue(out var plan) || !facts.Rooms.TryGetValue(out var rooms))
        {
            return (new Pol.BedroomSwap(), false);
        }
        return NextBedroomSwap(obs!, traits, SuiteRoomIds(plan, rooms));
    }

    /// <summary>
    /// Whether the bedroom ladder answers a sleeping choice: with no demand, and ahead of a barracks bed while
    /// colonists own only spots or no bed is buildable (bedrooms before bedrolls). A colonist owning no bed is
    /// unhoused there too.
    /// </summary>
    internal static bool BedroomsFirst(Pol.SleepingMethod method) =>
        method == Pol.SleepingMethod.NoDemand || method == Pol.SleepingMethod.Build || method == Pol.SleepingMethod.Unavailable;

    /// <summary>The stuff a bed step builds definition from: a bedroll's first stuff option in stock, else the cheapest the rows allow.</summary>
    internal static string BedStuff(Observation.ColonyProjection facts, string definition)
    {
        if (definition == SleepingBedrollDefinition || definition == SleepingCoupleBedrollDefinition)
        {
            if (facts.StockedStuff(definition) is { } stocked)
            {
                return stocked.Stuff;
            }
        }
        return facts.BuildStuff(definition);
    }
}
