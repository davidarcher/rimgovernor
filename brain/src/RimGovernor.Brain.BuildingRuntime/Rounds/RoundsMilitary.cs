using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the military family (#3259). The read gives the defense facts (the owned census with the
/// hostiles of its emergency read), the training review (the room census with the range and ring definitions) and
/// the pest facts (the acquisition census, the Need's own hunts and the next hunt), and the equipment facts (the
/// gear census, the crafting spot owed down and the mortar shell review). The work step cancels stale pest hunts and
/// files why no hunt was proposed, builds the training range and ring rooms under the training Need, and places or
/// retires the crafting spot and walks the replacement-gear workshop ladder under the equipment Need.
/// </summary>
public sealed class MilitaryRounds
{
    private readonly Rounder _reviewer;
    private readonly bool _defense;
    private readonly bool _pests;
    private readonly bool _training;
    private readonly bool _equipment;

    // Stages the training rooms; null for a source that cannot preview buildings.
    private readonly RoundsBuildingPlanner? _building;

    // The armory's crafting spot, wanted by its ledger declaration; null without the armory.
    private readonly RoundsArmoryPlanner? _armory;

    // The workshop ladder for replacement gear; null without the workshop or a workshop source.
    private readonly RoundsBuildingPlanner? _workshop;

    /// <summary>
    /// The defense, pests and training toggles compose those Needs; <paramref name="equipment"/> composes
    /// military/equipment, whose work step places the <paramref name="armory"/>'s crafting spot and, with
    /// <paramref name="workshop"/>, walks the replacement-gear workshop ladder.
    /// </summary>
    public MilitaryRounds(Rounder reviewer, bool defense, bool pests, bool training, bool equipment = false, RoundsArmoryPlanner? armory = null, bool workshop = false)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("MilitaryRounds: reviewer == nil || reviewer.native == nil");
        }
        if (!equipment && (armory is not null || workshop))
        {
            throw new ControlException("MilitaryRounds: equipment work is composed without the military/equipment Need");
        }
        _reviewer = reviewer;
        _defense = defense;
        _pests = pests;
        _training = training;
        _equipment = equipment;
        _armory = armory;
        if (training && reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source };
        }
        if (workshop && reviewer.Native is IRoundsWorkshopSource and Observation.IRoundsSource)
        {
            _workshop = RoundsBuildingPlanner.NewRoundsGearWorkshopPlanner(reviewer, (IRoundsBuildingSource)reviewer.Native);
        }
    }

    /// <summary>The family's scheduler entry: it wakes on its reads and on hunt, building, equip and dressing outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<MilitaryFacts>(new MilitaryFamily(_defense, _pests, _training, _equipment), ReadAsync, Schedule, WorkAsync);

    /// <summary>An optional entry woken by the outcomes of pest hunts, buildings, arming, apparel policies and outfit prunes.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.Acquisition, ActionKind.Building, ActionKind.Equip, ActionKind.ApparelPolicy, ActionKind.PolicyPrune, ActionKind.Deconstruction],
    };

    /// <summary>The definitions the range step reads availability and stuff for: the range's own buildings.</summary>
    internal static readonly string[] TrainingDefinitions = [RangeDefNames[RangePieceKind.Stand], RangeDefNames[RangePieceKind.Dummy], RingMarkerDef];

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. Throws when the scope is another world or older
    /// than the review. The immediate pass reads nothing: the family has no SeriousHarm Needs yet.
    /// </summary>
    internal async Task<MilitaryFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new MilitaryFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("military read: the scope is another world or older than the review");
        }
        var facts = new MilitaryFacts();
        if (_defense || _training || _equipment)
        {
            var reading = _training
                ? await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), TrainingDefinitions, call).ConfigureAwait(false)
                : await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), call).ConfigureAwait(false);
            var f = reading.Projection.Facts;
            var emergency = EmergencySnapshot.New(scope.World, expected.Tick, reading.Emergency);
            if (_equipment)
            {
                // The crafting spot owed down and the shell review, as the review derives them.
                var retire = await _reviewer.CraftingSpotRetireOwedAsync(scope.World, reading.Projection, call).ConfigureAwait(false);
                var shells = await ShellTargetsAsync(_reviewer.Native, _reviewer.Player.Journal, scope.World, reading.Projection, call).ConfigureAwait(false);
                f = f with { CraftingSpotRetireOwed = retire, ShellsShort = PolicyApi.ShellsShort(shells, reading.Projection.Resources) };
                // Each pawn's outfit filter as the latest review's comparator left it (#3285): allowed only where it rejected disallowing.
                if (f.Gear.TryGetValue(out var gear))
                {
                    var allowed = _reviewer.GearFilters.Get(scope.World);
                    f = f with { Gear = Known(gear with { Pawns = [.. gear.Pawns.Select(p => p with { AllowTainted = allowed.Contains(p.Pawn.Value) })] }) };
                }
            }
            facts = MilitaryFacts.From(f with { Hostiles = PolicyApi.EmergencyNeeds(emergency, scope.World, expected.Tick).Hostiles });
        }
        if (_pests)
        {
            facts = await PestFactsAsync(scope.World, review, facts, call).ConfigureAwait(false);
        }
        return facts;
    }

    /// <summary>
    /// The pest section: the census count, the Need's own hunts whose animal is gone, whether an outdoor hazard
    /// holds hunting, and the next hunt over every pest row not designated or already held by an undispatched hunt
    /// of the Need, within the hunting budget.
    /// </summary>
    private async Task<MilitaryFacts> PestFactsAsync(GenerationSnapshot world, RoundsReview review, MilitaryFacts facts, CancellationToken call)
    {
        var journal = _reviewer.Player.Journal;
        var (_, projection) = await _reviewer.AcquisitionReadingAsync(world, review, call).ConfigureAwait(false);
        var wild = projection.Facts.AnimalUpkeep.WildAnimals;
        var pests = PolicyApi.PestCensus(wild);
        var owner = journal.LoadNeed(MilitaryFamily.Pests, world, call);
        var stale = new List<StaleHunt>();
        var undispatched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in owner.Methods)
        {
            var plan = journal.LoadPlan(method.Plan, call);
            // An unissued hunt follows its animal wherever the census reports it; one whose animal died to the
            // defense or left the map would be held forever, so the work step cancels it.
            stale.AddRange(GonePestHunts(plan.Progress, wild).Select(action => new StaleHunt(method.Plan, action)));
            undispatched.UnionWith(UndispatchedAcquisitions(plan.Progress));
        }
        facts = facts with { Pests = pests, StaleHunts = [.. stale], HuntingHeld = PolicyApi.OutdoorAcquisitionHeld(projection.Facts) };
        if (!pests.TryGetValue(out var count) || count == 0)
        {
            return facts;
        }
        // A designated or undispatched pest row is held: it is not planned again.
        var held = new Dictionary<string, bool>(StringComparer.Ordinal);
        _ = HoldWorked(projection.Acquisition, undispatched, held);
        var (slots, noHunter) = RoundsResourcePlanner.HuntSlots(projection);
        IReadOnlyList<AcquisitionSource> selected;
        try
        {
            selected = PolicyApi.SelectPestAcquisition(projection.Acquisition, pests, held, slots);
        }
        catch (DomainException)
        {
            return facts with { HuntsUnread = true };
        }
        if (selected.Count == 0)
        {
            return facts with { HuntersMissing = noHunter };
        }
        var hash = new System.Text.StringBuilder();
        foreach (var row in selected)
        {
            hash.Append(CultureInfo.InvariantCulture, $"{row.Id}/{row.Resource}/{row.Token}/{row.Cell.X}/{row.Cell.Z}\n");
        }
        // The MethodId names the selected rows: the same selection continues its open hunt.
        return facts with { Hunt = new PestHunt(new MethodId("pest-hunt-" + HashHex16(hash.ToString())), [.. selected]) };
    }

    /// <summary>
    /// The work step of a ready Need: pests cancels its stale hunts and says why it proposed none; training builds
    /// its rooms; equipment places or retires its crafting spot and walks its workshop ladder.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, MilitaryFacts facts, CancellationToken call)
    {
        if (work.Need.Id == MilitaryFamily.Pests)
        {
            return PestWork(work, facts, call);
        }
        if (work.Need.Id == MilitaryFamily.Training)
        {
            return await TrainingWorkAsync(work, call).ConfigureAwait(false);
        }
        if (work.Need.Id == MilitaryFamily.Equipment)
        {
            return await EquipmentWorkAsync(work, facts, call).ConfigureAwait(false);
        }
        return NoMethod(work.Need.Id);
    }

    /// <summary>
    /// The equipment Need's buildings: the crafting spot its armory declaration wants, then the workshop ladder for
    /// replacement gear, each held only by its claims and its own MethodIds. The Need reports the first admission,
    /// else the first step that names its reason; with neither, gear still short is the unsupported replacement.
    /// </summary>
    private async Task<Verdict> EquipmentWorkAsync(NeedWork work, MilitaryFacts facts, CancellationToken call)
    {
        var verdicts = new List<Verdict>();
        if (_armory is not null)
        {
            verdicts.Add(await _armory.PlaceCraftingSpotAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false));
        }
        if (_workshop is not null)
        {
            var result = await _workshop.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false);
            verdicts.Add(result.Decision?.Admitted == true ? BuildingReasonAdmitted : result.Verdict);
        }
        return EquipmentVerdict(verdicts, PolicyApi.ReviewGear(facts.Gear).Recovered);
    }

    /// <summary>
    /// The first admission among the equipment steps' verdicts, else the first that is more than nothing to do;
    /// otherwise gear the review does not find recovered is held as the unsupported apparel replacement, and the
    /// rest (shells, which the ledger orders) is no method.
    /// </summary>
    internal static Verdict EquipmentVerdict(IReadOnlyList<Verdict> verdicts, Fact<bool> gearRecovered)
    {
        foreach (var verdict in verdicts)
        {
            if (verdict == BuildingReasonAdmitted)
            {
                return verdict;
            }
        }
        foreach (var verdict in verdicts)
        {
            if (verdict != BuildingReasonNoDeficit && verdict != BuildingReasonDisabled && verdict != BuildingReasonExistingFacility)
            {
                return verdict;
            }
        }
        if (gearRecovered.TryGetValue(out var recovered) && !recovered)
        {
            return AwaitingPlan(MilitaryFamily.UnsupportedApparelReplacement,
                "Observed apparel replacement demand requires shared forecast worker, timing and outcome alternatives.");
        }
        return NoMethod(MilitaryFamily.Equipment);
    }

    private Verdict PestWork(NeedWork work, MilitaryFacts facts, CancellationToken call)
    {
        foreach (var hunt in facts.StaleHunts)
        {
            _reviewer.Player.Journal.Cancel(hunt.Plan, hunt.Action, call);
        }
        if (facts.HuntingHeld)
        {
            // An outdoor hazard holds chop, hunt and forage like it holds remote work.
            return WaitFor(Cause.RoomTemperature, PolicyConstants.RemoteHoldHazard);
        }
        if (facts.HuntsUnread)
        {
            return FieldUnavailable("acquisition_sources");
        }
        return facts.Hunt is null && facts.HuntersMissing ? NoWorker("hunter") : NoMethod(work.Need.Id);
    }

    /// <summary>Reconciles the layout's range and ring rooms under the training Need, the range first; work staged ends the step.</summary>
    private async Task<Verdict> TrainingWorkAsync(NeedWork work, CancellationToken call)
    {
        if (_building is null)
        {
            return FieldUnavailable("building_source");
        }
        var state = work.State;
        var review = _reviewer.Player.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return BuildingReasonNoReview;
        }
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("training work: the native boundary moved");
        }
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), TrainingDefinitions, call).ConfigureAwait(false);
        if (!reading.Projection.LayoutPlan.TryGetValue(out var layout))
        {
            return FieldUnavailable("layout_plan");
        }
        var rooms = new List<RoomReconcile>();
        var ranges = layout.RangeRooms();
        if (ranges.Count > 0)
        {
            var room = ranges[0];
            rooms.Add(new RoomReconcile { Room = room, Template = RangeTemplate(room), Name = RoomName("range", room), Reason = "range" });
        }
        var rings = layout.RingRooms();
        if (rings.Count > 0)
        {
            var room = rings[0];
            rooms.Add(new RoomReconcile { Room = room, Template = RingTemplate(room), Name = RoomName("ring", room), Reason = "ring" });
        }
        if (rooms.Count == 0)
        {
            // The layout review grows the range and ring from RoomDemand.
            return WaitFor(Cause.MethodUsed, "range_room");
        }
        var stock = new PackedStock(_reviewer.Native, Boundary.Identity(state.Snapshot));
        var verdict = new Verdict();
        foreach (var rr in rooms)
        {
            var result = await _building.ReconcileRoomAsync(state, review, work.Owner, reading, stock, rr, call, work.Epoch).ConfigureAwait(false);
            verdict = result.Verdict;
            // A room with nothing ready (finished, or waiting on its builders) leaves the next one its turn; work staged ends the step.
            if (!verdict.Is(Cause.ExistingWork))
            {
                break;
            }
        }
        return verdict;
    }

    private static Verdict NoMethod(NeedId need) => AwaitingPlan(need.Value, "no_method");

    /// <summary>The range's stands and the ring's markers the review wants, read from the projection's work pawns; unknown while they are.</summary>
    internal static (Fact<int> Stands, Fact<int> Rings) TrainingDemand(Observation.ColonyProjection facts)
    {
        if (!facts.WorkPawns.TryGetValue(out _) || !ReviewTraining(facts.Facts).TryGetValue(out var review))
        {
            return (Unknown<int>(), Unknown<int>());
        }
        return (Known(review.ShootingBelow > 0 ? RangeStandsFor(review.Capable) : 0),
            Known(review.MeleeBelow > 0 ? RingMarkersFor(Math.Max(2, review.Capable)) : 0));
    }
}
