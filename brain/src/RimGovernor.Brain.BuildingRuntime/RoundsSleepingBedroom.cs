namespace RimGovernor.Brain.BuildingRuntime;

// Individual bedrooms. The sleeping Need (shelter/sleeping) stays unmet while a bedroom step is due; the sleeping planner
// answers it once every colonist owns a bed: move, furnish, then shell (NextBedroomStep).

partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// The projection's next bedroom step: none without the layout plan, the room census or the sleeping
    /// census. A tribe builds its wood bedrooms at Camp tier too.
    /// </summary>
    internal static Pol.BedroomStep BedroomStepOf(Observation.ColonyProjection facts)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sleeping = facts.Facts.Sleeping;
        if (!pk || !rk)
        {
            return new Pol.BedroomStep();
        }
        return SleepingRules.NextBedroomStep(plan!, rooms!, sleeping!, BedroomTargets(facts), SleepingTraits(facts), SuitePressure(facts), Pol.BedroomGainFacts.Of(facts.Facts));
    }

    /// <summary>
    /// Per planned bedroom, the colonists the bedroom step would move into it (#3655), the largest priced gain first (#3656); and
    /// per planned throne room or suite, the colonist it serves (any suite occupant, #3826) and its title's requirement (#3807,
    /// RoyalRoomRules.PlannedRoyalRooms: the suite step's claims in its order, the owed throne room's holder). None without the
    /// layout plan, the room census or the sleeping census. An unread gain is named in <paramref name="findings"/>.
    /// </summary>
    internal static (IReadOnlyDictionary<int, EquatableArray<string>> Bedrooms, IReadOnlyDictionary<int, Pol.RoyalRoomNeed> Royal) PlannedOccupantsOf(
        Observation.ColonyProjection facts, ISet<string> findings)
    {
        var sleeping = facts.Facts.Sleeping;
        if (facts.LayoutPlan is not { } plan || !facts.Rooms.TryGetValue(out var rooms))
        {
            return (new Dictionary<int, EquatableArray<string>>(), new Dictionary<int, Pol.RoyalRoomNeed>());
        }
        var targets = BedroomTargets(facts);
        // The suite queue as SuiteWishesOf orders it; an unread trait census only drops the ascetic skip, and an ascetic has no title, Greedy or Jealous room thought.
        var claims = RoomQualityRules.SuiteClaims(plan!, rooms!, sleeping!, targets ?? new Dictionary<string, Pol.RoomTarget>(),
            SleepingTraits(facts) ?? new Dictionary<PawnId, Pol.TraitEffects>(), SuitePressure(facts) ?? new Dictionary<PawnId, double>());
        var (throne, owed) = ThroneNeed(facts);
        return (SleepingRules.PlannedBedroomOccupants(plan!, rooms!, sleeping!, targets, Pol.BedroomGainFacts.Of(facts.Facts), findings),
            Pol.RoyalRoomRules.PlannedRoyalRooms(plan!, rooms!, sleeping!, claims, owed ? throne : null));
    }

    /// <summary>The projection's next wing migration step: none without the layout plan, the room census or the sleeping census.</summary>
    internal static Pol.BedroomStep MigrateStep(Observation.ColonyProjection facts)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sleeping = facts.Facts.Sleeping;
        if (!pk || !rk)
        {
            return new Pol.BedroomStep();
        }
        return SleepingRules.NextMigrateStep(plan!, rooms!, sleeping!);
    }

    /// <summary>The rooms' quality targets, so a bedroom move leaves an ascetic's NeverUpgrade room alone; null while the census is unknown.</summary>
    internal static Dictionary<string, Pol.RoomTarget>? BedroomTargets(Observation.ColonyProjection facts)
    {
        var obs = facts.Facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (traits is null)
        {
            return null;
        }
        facts.TechTier.TryGetValue(out var tier);
        return RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness);
    }

    /// <summary>
    /// The target-sized bedrooms the station graph sizes (#2939): the plan's suites with their owners' targets
    /// and the claims no vacant suite answers; null while the room, sleeping or work census is unknown.
    /// </summary>
    internal static IReadOnlyList<Pol.SuiteWish>? SuiteWishesOf(Observation.ColonyProjection facts, Pol.LayoutPlan plan)
    {
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sleeping = facts.Facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (!rk || traits is null)
        {
            return null;
        }
        var targets = BedroomTargets(facts) ?? new Dictionary<string, Pol.RoomTarget>();
        var claims = RoomQualityRules.SuiteClaims(plan, rooms!, sleeping!, targets, traits, SuitePressure(facts) ?? new Dictionary<PawnId, double>());
        return RoomQualityRules.SuiteWishes(plan, rooms!, sleeping!, targets, claims);
    }

    /// <summary>Excludes pawns awaiting a suite; their current rooms receive no in-place quality upgrade.</summary>
    internal static IReadOnlyDictionary<string, Pol.RoomTarget> UpgradeTargetsOf(Observation.ColonyProjection facts, IReadOnlyDictionary<string, Pol.RoomTarget>? targets)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sleeping = facts.Facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (!pk || !rk || traits is null)
        {
            return targets ?? new Dictionary<string, Pol.RoomTarget>();
        }
        var claims = RoomQualityRules.SuiteClaims(plan!, rooms!, sleeping!, BedroomTargets(facts) ?? new Dictionary<string, Pol.RoomTarget>(), traits, SuitePressure(facts) ?? new Dictionary<PawnId, double>());
        return RoomQualityRules.UpgradeTargets(targets ?? new Dictionary<string, Pol.RoomTarget>(), sleeping!, claims);
    }

    /// <summary>
    /// The review's BedroomsOwed for the projection. A due room quality swap or wing migration owes a
    /// bedroom too, so the sleeping Need stays unmet until it is done.
    /// </summary>
    internal static bool BedroomsOwedOf(Observation.ColonyProjection facts)
    {
        if (SleepingRules.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping, BedroomTargets(facts), SleepingTraits(facts), SuitePressure(facts)))
        {
            return true;
        }
        if (facts.LayoutPlan is null || !facts.Rooms.IsKnown)
        {
            return false;
        }
        return BedroomSwapOf(facts).Ok
            || MigrateStep(facts).Kind != Pol.BedroomStepKind.None
            || RoomUpgradeOf(facts).Due
            || CompanionBed(facts).Due
            || ThroneStepOf(facts).Owed
            || ChildRoomStep(facts).Owed();
    }

    /// <summary>
    /// Prefixes every planned room's reconcile methods: "&lt;role&gt;-shell-room&lt;id&gt;". The in-flight census reads a
    /// room's open work back by this stem (Rounder.RoomMethodIds), so a room keeps its history across a re-site; the
    /// sleeping acceptance cases read the journal by the bedroom's. The build side appends the wave (removal, install, build).
    /// </summary>
    internal static string RoomReconcileName(string role, Pol.PlannedRoom room) =>
        string.Create(CultureInfo.InvariantCulture, $"{role}-shell-room{room.Id}");

    /// <summary>A bedroom shell's short why: the colonists still outside a bedroom.</summary>
    internal static string BedroomShellReason(Pol.BedroomStep step) =>
        step.Unhoused <= 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"room for {step.Unhoused} unhoused");

    /// <summary>Orders the suite queue by the pawns' current bedroom, space and jealousy thoughts; null (pawn-id order) while the mood census is unknown.</summary>
    internal static Dictionary<PawnId, double>? SuitePressure(Observation.ColonyProjection facts) =>
        facts.Facts.MoodPawns.TryGetValue(out var pawns) ? SuitePriorityRules.SuitePressure(pawns) : null;
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// Answers a BedroomReconcile through the shared build side: the room's ring, doors and floor and its bed
    /// are reconciled to the plan and the bedroom template, the bed installed from packed stock first and built
    /// on site only when none is stored. A vacant bed left in the starter shell is packed to become that stock;
    /// a placement native refuses means wait.
    /// </summary>
    internal async Task<RoundsBuildingResult> ReconcileBedroomAsync(PackedStock stock, ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.RoundsReading reading, Pol.BedroomStep step, CancellationToken call, CancellationToken epoch)
    {
        var facts = reading.Projection;
        // A suite is only started with its whole ring in stock.
        if (step.Room.Role == Pol.PlannedRole.Suite)
        {
            var inside = step.Room.Region.Bounds();
            var walls = facts.StockedStuff("Wall")?.Count ?? 0;
            if (walls < 2 * (inside.Width + inside.Height) + 4)
            {
                return new RoundsBuildingResult { Verdict = BuildingReasonSuiteStock };
            }
        }
        var request = SleepingRequest(facts, review);
        var (bed, method) = SleepingRules.SleepingDefinition(facts.Shapes.Furniture, request.Definitions, request.Stocked, false, true);
        if (method != Pol.SleepingMethod.Build)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonSleepingUnavailable };
        }
        if (!facts.Rooms.TryGetValue(out var rooms))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("room_census") };
        }
        if (facts.LayoutPlan is not { } plan)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("layout_plan") };
        }
        // A barracks is furnished to its tiled bed slots, a bed each.
        var furnishing = SleepingRules.BedroomFurnishing(step.Room, rooms.Shapes, bed, StandingBuildings(facts), null, SleepingRules.BedSlotsOf(plan, step.Room, rooms.Shapes.Furniture));
        // A bed standing in an unenclosed room still owes the room its ring; the diff skips the pieces that stand.
        if (!SleepingRules.BedroomReconcileOwed(step.Room, rooms, furnishing))
        {
            return new RoundsBuildingResult { Verdict = NoSpace(SleepingRules.BedroomTemplateSubject) };
        }
        return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = furnishing.Pieces, Name = RoomReconcileName("bedroom", step.Room), Reason = BedroomShellReason(step) }, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a Reconcile step over its owed rooms in plan order (#3743): a room the build side holds (AwaitingPlan, the
    /// portfolio's hold among them) gives way to the next; the first result that is not AwaitingPlan, else the first one.
    /// </summary>
    internal static async Task<RoundsBuildingResult> FirstUnheldRoomAsync(IReadOnlyList<Pol.PlannedRoom> owed, Func<Pol.PlannedRoom, Task<RoundsBuildingResult>> reconcile)
    {
        RoundsBuildingResult? first = null;
        foreach (var room in owed)
        {
            var result = await reconcile(room).ConfigureAwait(false);
            if (!result.Verdict.Is(Cause.AwaitingPlan))
            {
                return result;
            }
            first ??= result;
        }
        return first ?? new RoundsBuildingResult();
    }
}
