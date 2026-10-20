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
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!pk || !rk || !sk)
        {
            return new Pol.BedroomStep();
        }
        return SleepingRules.NextBedroomStep(plan!, rooms!, sleeping!, BedroomTargets(facts), SleepingTraits(facts), SuitePressure(facts), Pol.BedroomGainFacts.Of(facts.Facts));
    }

    /// <summary>
    /// Per planned bedroom, the colonists the bedroom step would move into it (#3655), the largest priced gain first (#3656);
    /// none without the layout plan, the room census or the sleeping census. An unread gain is named in <paramref name="findings"/>.
    /// </summary>
    internal static IReadOnlyDictionary<int, EquatableArray<string>> BedroomOccupantsOf(Observation.ColonyProjection facts, ISet<string> findings)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan) || !facts.Rooms.TryGetValue(out var rooms) || !facts.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            return new Dictionary<int, EquatableArray<string>>();
        }
        return SleepingRules.PlannedBedroomOccupants(plan!, rooms!, sleeping!, BedroomTargets(facts), Pol.BedroomGainFacts.Of(facts.Facts), findings);
    }

    /// <summary>The projection's next wing migration step: none without the layout plan, the room census or the sleeping census.</summary>
    internal static Pol.BedroomStep MigrateStep(Observation.ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!pk || !rk || !sk)
        {
            return new Pol.BedroomStep();
        }
        return SleepingRules.NextMigrateStep(plan!, rooms!, sleeping!);
    }

    /// <summary>The rooms' quality targets, so a bedroom move leaves an ascetic's NeverUpgrade room alone; null while the census is unknown.</summary>
    internal static Dictionary<string, Pol.RoomTarget>? BedroomTargets(Observation.ColonyProjection facts)
    {
        var known = facts.Facts.Sleeping.TryGetValue(out var obs);
        var traits = SleepingTraits(facts);
        if (!known || traits is null)
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
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var traits = SleepingTraits(facts);
        if (!rk || !sk || traits is null)
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
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var traits = SleepingTraits(facts);
        if (!pk || !rk || !sk || traits is null)
        {
            return targets ?? new Dictionary<string, Pol.RoomTarget>();
        }
        var claims = RoomQualityRules.SuiteClaims(plan!, rooms!, sleeping!, BedroomTargets(facts) ?? new Dictionary<string, Pol.RoomTarget>(), traits, SuitePressure(facts) ?? new Dictionary<PawnId, double>());
        return RoomQualityRules.UpgradeTargets(targets ?? new Dictionary<string, Pol.RoomTarget>(), sleeping!, claims);
    }

    /// <summary>
    /// The review's BedroomsOwed fact for the projection. A due room quality swap or wing migration owes a
    /// bedroom too, so the sleeping Need stays unmet until it is done.
    /// </summary>
    internal static Fact<bool> BedroomsOwedOf(Observation.ColonyProjection facts)
    {
        var owed = SleepingRules.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping, BedroomTargets(facts), SleepingTraits(facts), SuitePressure(facts));
        if (owed.TryGetValue(out var v) && !v)
        {
            if (BedroomSwapOf(facts).Ok)
            {
                return Known<bool>(true);
            }
            if (MigrateStep(facts).Kind != Pol.BedroomStepKind.None)
            {
                return Known<bool>(true);
            }
            if (RoomUpgradeOf(facts).Due)
            {
                return Known<bool>(true);
            }
            if (CompanionBed(facts).Due)
            {
                return Known<bool>(true);
            }
            if (ThroneStepOf(facts).Owed || ChildRoomStep(facts).Owed())
            {
                return Known<bool>(true);
            }
        }
        return owed;
    }

    /// <summary>
    /// Prefixes a bedroom room's reconcile methods. The "bedroom-shell-room&lt;id&gt;" stem is the one the sleeping
    /// acceptance cases read the journal by; the build side appends the wave (removal, install, build).
    /// </summary>
    internal static string BedroomReconcileName(Pol.PlannedRoom room)
    {
        return string.Create(CultureInfo.InvariantCulture, $"bedroom-shell-room{room.Id}");
    }

    /// <summary>A bedroom shell's short why: the colonists still outside a bedroom.</summary>
    internal static string BedroomShellReason(Pol.BedroomStep step) =>
        step.Unhoused <= 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"room for {step.Unhoused} unhoused");

    /// <summary>Builds both from the one stuff the colony can raise a shell from (ShellSharedStuff).</summary>
    internal static (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) SharedShellStuff(Observation.ColonyProjection facts, Observation.PlanningDefinition wall, Observation.PlanningDefinition door)
    {
        if (RoundsAnimalContainment.ShellSharedStuff(facts, wall, door) is not { } stuff)
        {
            return ("", "", FieldUnavailable("wall_door_stuff"), false);
        }
        return (stuff, stuff, new Verdict(), true);
    }

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
        if (method == Pol.SleepingMethod.Unknown)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("bed_definitions") };
        }
        if (method != Pol.SleepingMethod.Build)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonSleepingUnavailable };
        }
        if (!facts.Rooms.TryGetValue(out var rooms))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("room_census") };
        }
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("layout_plan") };
        }
        // A barracks is furnished to its tiled bed slots, a bed each.
        var furnishing = SleepingRules.BedroomFurnishing(step.Room, rooms.Shapes, bed, StandingBuildings(facts), null, SleepingRules.BedSlotsOf(plan, step.Room, rooms.Shapes.Furniture));
        if (!furnishing.Absent)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("bedroom_template") };
        }
        return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = furnishing.Pieces, Name = BedroomReconcileName(step.Room), Reason = BedroomShellReason(step) }, call, epoch).ConfigureAwait(false);
    }
}
