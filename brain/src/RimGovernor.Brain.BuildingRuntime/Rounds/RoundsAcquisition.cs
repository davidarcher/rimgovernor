using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsAcquisitionResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>
/// Plans one acquisition goal: EnsureFoodSupply harvests and hunts toward its food plan; ClearPests hunts every
/// recognised pest the wild-animal census reports, one hunt method per admission, until none remain;
/// MaintainResource chops, forages and hunts toward its ranked floors through the acquisition catalog, beside
/// RoundsResourcePlanner's bills and mines.
/// </summary>
public sealed class RoundsAcquisitionPlanner
{
    private readonly Rounder _reviewer;
    private readonly ConcernId _need;

    public RoundsAcquisitionPlanner(Rounder reviewer, ConcernId need)
    {
        if (reviewer is null || (need != PolicyApi.EnsureFoodSupply && need != PolicyApi.ClearPests && need != PolicyApi.MaintainResource))
        {
            throw new ControlException("NewRoundsAcquisitionPlanner: reviewer == nil || (need != policy.EnsureFoodSupply && need != policy.ClearPests && need != policy.Maintain");
        }
        _reviewer = reviewer;
        _need = need;
    }

    internal RoundsAcquisitionResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsAcquisitionResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsAcquisitionResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, _need, call);
        if (!workable)
        {
            return new RoundsAcquisitionResult { Verdict = BuildingReasonNoDeficit };
        }

        var (expected, projection) = _reviewer.AcquisitionReading(call, state, review);
        // An outdoor hazard holds chop, hunt and forage like it holds remote work.
        if (PolicyApi.OutdoorAcquisitionHeld(projection.Facts))
        {
            return new RoundsAcquisitionResult { Verdict = WaitFor(PolicyApi.CauseRoomTemperature, PolicyApi.RemoteHoldHazard) };
        }
        var pest = _need == PolicyApi.ClearPests;
        var stockGoal = _need == PolicyApi.MaintainResource;
        var food = _need == PolicyApi.EnsureFoodSupply;
        var huntOnly = false;
        var pests = PolicyApi.PestCensus(projection.Facts.AnimalUpkeep.WildAnimals);
        var stalledSources = new HashSet<string>(StringComparer.Ordinal);
        // A source a stall rotated away from stays keyed out for its contract's bounded cooldown: the goal's
        // progress record carries the key, and RecordProgressCooldown adds one under this review's revision.
        var progress = review.ConcernProgress(_need);
        var cooled = new HashSet<string>(StringComparer.Ordinal);
        // undispatched holds the goal's admitted acquisitions native has not designated yet (Pending/Prepared):
        // they block and hold their source like a designated census row until dispatch, when the census takes
        // over. Without it the gap re-admits the same source.
        var undispatched = new HashSet<string>(StringComparer.Ordinal);

        void Cool(ProgressContract contract, string thing)
        {
            var key = PolicyApi.CooldownKey(contract.Method, thing);
            cooled.Add(thing);
            try
            {
                review = p.Journal.RecordProgressCooldown(review.Revision, _need, key, contract.CooldownUntil(expected.Tick), call);
            }
            catch (ConflictException)
            {
            }
        }

        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (pest)
            {
                // An unissued pest hunt follows its animal wherever the census reports it; one whose animal died
                // to the defense or left the map would be held forever and the goal behind it, so it is cancelled at once.
                foreach (var action in GonePestHunts(plan.Progress, projection.Facts.AnimalUpkeep.WildAnimals))
                {
                    p.Journal.Cancel(method.Plan, action, call);
                }
            }
            foreach (var thing in UndispatchedAcquisitions(plan.Progress))
            {
                undispatched.Add(thing);
            }
        }
        // Stalls are read from the census alone: any designated row of the goal's kind, untaken past its contract
        // since native first saw it, the player's own included. Each is withdrawn by its own one-action method,
        // admitted before re-selection and using up this step's admission. A pest hunt follows its animal and
        // native settles it on the animal's death or departure, so the hunt-stall rule is not its exit: the pest
        // goal withdraws nothing.
        if (!pest)
        {
            Func<AcquisitionSource, bool> mine = row => food && row.Food;
            if (stockGoal)
            {
                var targets = _reviewer.ResourceTargets(state.Snapshot);
                mine = row => targets.ContainsKey(new Resource(row.Resource));
            }
            foreach (var hunt in new[] { false, true })
            {
                var contract = hunt ? _reviewer.Policy.HuntProgress() : _reviewer.Policy.AcquisitionProgress();
                foreach (var row in StalledDesignations(projection.Acquisition, hunt, expected.Tick, contract, mine))
                {
                    stalledSources.Add(row.Id);
                    var (method, plan) = StallWithdraw(row);
                    try
                    {
                        p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
                        continue;
                    }
                    catch (NotFoundException)
                    {
                    }
                    Cool(contract, row.Id);
                    p.Current(call, epoch);
                    if (p.Session.State() != state)
                    {
                        throw new ControlException("step: p.session.State() != state");
                    }
                    p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, "withdraw stalled " + row.Resource, plan, call);
                    return new RoundsAcquisitionResult { Verdict = BuildingReasonAdmitted, Plan = plan.Id };
                }
            }
        }
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (!pest)
        {
            held = CooledSources(projection.Acquisition, _reviewer.ChopMinGrowth(projection), id => cooled.Contains(id) || AcquisitionCooled(progress, _reviewer.Policy, id, expected.Tick));
        }
        // Blocking reads the census, not the journal: a designated row the goal would plan is its ExistingWork. A
        // source this step cancelled or cooled still reads designated until native withdraws it, and never
        // blocks. ClearPests is never blocked: a hunt already designated holds its own animal (held, below) and
        // counts against the two outstanding hunts (slots), but does not stop the next animal being planned.
        // EnsureFoodSupply's hunts are the exception: a forage batch harvests one bush at a time for days while
        // the hunt rows the butcher spot and bill were placed for wait behind it, so designated plant harvests
        // leave the hunt slots plannable and only hunts are admitted over them. MaintainResource skips a
        // designated resource in resourceSelection instead; its other targets still plan.
        if (!pest && !stockGoal)
        {
            var (block, plants) = CensusBlocking(projection.Acquisition, food, held, undispatched);
            if (block)
            {
                return new RoundsAcquisitionResult { Verdict = BuildingReasonExistingWork };
            }
            huntOnly = plants;
        }
        // A designated or undispatched source is held: it is not planned again. Its resource is busy for
        // MaintainResource unless a cooldown held it.
        var busy = HoldWorked(projection.Acquisition, undispatched, held);
        var pending = WithoutStalled(projection.PendingFoodNutrition, projection.Acquisition, stalledSources, food);
        var deficit = Unknown<double>();
        var runway = Unknown<double>();
        if (food)
        {
            if (!projection.Facts.FoodPlan.TryGetValue(out var foodPlan))
            {
                return new RoundsAcquisitionResult { Verdict = FieldUnavailable("food_plan") };
            }
            runway = foodPlan.Forecast.RunwayDays;
            (projection.Acquisition, deficit) = FoodPlanAcquisition(foodPlan, projection.Acquisition);
        }
        var (slots, noHunter) = HuntSlots(projection);
        IReadOnlyList<AcquisitionSource> selected;
        var existing = false;
        try
        {
            if (stockGoal)
            {
                var supply = _reviewer.ResourceSupply(call, state, review, goal);
                (selected, existing) = ResourceSelection(supply, busy);
            }
            else if (pest)
            {
                selected = PolicyApi.SelectPestAcquisition(projection.Acquisition, pests, held, slots);
            }
            else
            {
                var sources = projection.Acquisition;
                if (huntOnly)
                {
                    sources = HuntRows(sources);
                }
                selected = PolicyApi.SelectAcquisition(sources, deficit, pending, food, held, slots);
            }
        }
        catch (DomainException)
        {
            return new RoundsAcquisitionResult { Verdict = FieldUnavailable(UnreadAcquisitionFact(projection.Acquisition, deficit, pending, pest || stockGoal)) };
        }
        if (selected.Count == 0 && existing)
        {
            return new RoundsAcquisitionResult { Verdict = BuildingReasonExistingWork };
        }
        if (selected.Count == 0)
        {
            return new RoundsAcquisitionResult { Verdict = NoAcquisition(_need, noHunter, deficit, pending) };
        }
        var hash = new System.Text.StringBuilder();
        foreach (var row in selected)
        {
            hash.Append(CultureInfo.InvariantCulture, $"{row.Id}/{row.Resource}/{row.Token}/{row.Cell.X}/{row.Cell.Z}\n");
        }
        var prefix = "acquire";
        if (pest)
        {
            // Every admission the goal ever made salts the pest method: a cancelled hunt of an animal that came
            // back to the same cell in the same state rehashes to a fresh method instead of reading as already used.
            hash.Append(CultureInfo.InvariantCulture, $"#{goal.Admitted}\n");
            prefix = "pest-hunt";
        }
        if (stockGoal)
        {
            prefix = "resource-acquire";
        }
        var methodId = new MethodId($"{prefix}-{HashHex16(hash.ToString())}");
        try
        {
            p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, methodId, call);
            return new RoundsAcquisitionResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "acquisition_method") };
        }
        catch (NotFoundException)
        {
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < selected.Count; i++)
        {
            var row = selected[i];
            var value = new Acquisition(row.Id, row.Resource, row.Cell);
            actions.Add(DomainAction.NewAcquisitionAction(new ActionId($"{id}-{i}"), value));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, methodId, AcquisitionReason(food, pest, runway, selected), spec, call);
        return new RoundsAcquisitionResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// MaintainResource's census selection: the chop, harvest and hunt sources the Round's supply plan opened,
    /// for the first floor (worst covered first) that has some and no designated work in flight. existing
    /// reports a floor passed over for its designated work (busy).
    /// </summary>
    internal static (IReadOnlyList<AcquisitionSource> Selected, bool Existing) ResourceSelection(ResourceSupply supply, HashSet<string> busy)
    {
        var existing = false;
        foreach (var resource in supply.Order)
        {
            // A designated resource is its own existing work; the goal's other targets still plan.
            if (busy.Contains(resource.Value))
            {
                existing = true;
                continue;
            }
            var picked = supply.Acquisitions(resource);
            if (picked.Count == 0)
            {
                continue;
            }
            return (picked, false);
        }
        return ([], existing);
    }

    /// <summary>
    /// Only admitted sources can start new work. Pending designations retain their native credit in
    /// SelectAcquisition; the ledger never calls them delivered.
    /// </summary>
    internal static (Fact<IReadOnlyList<AcquisitionSource>> Sources, Fact<double> Deficit) FoodPlanAcquisition(FoodPlan plan, Fact<IReadOnlyList<AcquisitionSource>> sources)
    {
        if (!sources.TryGetValue(out var rows))
        {
            return (Unknown<IReadOnlyList<AcquisitionSource>>(), Unknown<double>());
        }
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in plan.Portfolio)
        {
            // A formation hunt executes as a HuntRequest, never as designations.
            if ((entry.Channel.Kind == CandidateKind.Forage || (entry.Channel.Kind == CandidateKind.Hunt && entry.Channel.Mode() == HuntMode.Lone)) && entry.Decision == FoodPlanDecision.Open)
            {
                open.Add(entry.Channel.Id);
            }
        }
        var selected = new List<AcquisitionSource>();
        var nutrition = 0.0;
        foreach (var row in rows)
        {
            if (open.Contains(row.Id))
            {
                selected.Add(row);
                nutrition += row.NutritionYield;
            }
        }
        return (Known<IReadOnlyList<AcquisitionSource>>(selected), Known(nutrition));
    }

    /// <summary>
    /// Finds the unissued (pending or prepared) hunt actions of a pest plan whose animal the wild-animal census
    /// no longer lists (dead or off the map). The census has to be known: an unknown census is not evidence the
    /// pack is gone. A pest that is merely ineligible this step (no free hunter) or has wandered off its planned
    /// cell keeps its row in the wild census and its hunt, which follows it.
    /// </summary>
    internal static List<ActionId> GonePestHunts(IReadOnlyList<Progress> progress, Fact<IReadOnlyList<UpkeepAnimal>> wild)
    {
        var gone = new List<ActionId>();
        if (!wild.TryGetValue(out var animals))
        {
            return gone;
        }
        var alive = new HashSet<string>(animals.Select(a => a.Id.ToString()), StringComparer.Ordinal);
        foreach (var p in progress)
        {
            var acquisition = p.Action.Acquisition;
            var v = p.View;
            if (acquisition is not null && (v.Stage == Stage.Pending || v.Stage == Stage.Prepared) && !alive.Contains(acquisition.Thing))
            {
                gone.Add(v.Action);
            }
        }
        return gone;
    }

    /// <summary>
    /// Reads the goal's designated census rows, and the rows of its undispatched acquisitions, skipping held
    /// ones: block when a designated row is one the goal would plan, except that for food a designated plant
    /// harvest only restricts the goal to hunts (plants). The wood goal counts designated trees yielding wood.
    /// </summary>
    internal static (bool Block, bool Plants) CensusBlocking(Fact<IReadOnlyList<AcquisitionSource>> sources, bool food, HashSet<string> held, HashSet<string> undispatched)
    {
        sources.TryGetValue(out var rows);
        var plants = false;
        foreach (var row in rows ?? [])
        {
            if (!(row.Designated || undispatched.Contains(row.Id)) || held.Contains(row.Id))
            {
                continue;
            }
            if (food && row.Food && !row.Hunt)
            {
                plants = true;
            }
            else if ((food && row.Food) || (!food && row.Tree && row.Resource == "WoodLog"))
            {
                return (true, plants);
            }
        }
        return (false, plants);
    }

    /// <summary>The source things of the plan's acquisition actions not yet dispatched (Pending or Prepared).</summary>
    internal static HashSet<string> UndispatchedAcquisitions(IReadOnlyList<Progress> progress)
    {
        var things = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in progress)
        {
            var acquisition = p.Action.Acquisition;
            var stage = p.View.Stage;
            if (acquisition is not null && (stage == Stage.Pending || stage == Stage.Prepared))
            {
                things.Add(acquisition.Thing);
            }
        }
        return things;
    }

    /// <summary>Reports whether a source sits under a stall cooldown of either contract.</summary>
    internal static bool AcquisitionCooled(ConcernProgress progress, RoundsPolicy p, string thing, Tick tick) =>
        progress.Cooled(PolicyApi.CooldownKey(p.HuntProgress().Method, thing), tick) || progress.Cooled(PolicyApi.CooldownKey(p.AcquisitionProgress().Method, thing), tick);

    /// <summary>
    /// The census rows cooled reports: passed over like held ones, the cooldown lifting by itself at its bound,
    /// never a permanent ban.
    /// </summary>
    internal static HashSet<string> CooledSources(Fact<IReadOnlyList<AcquisitionSource>> sources, double minGrowth, Func<string, bool> cooled)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (sources.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                // An undesignated plantation tree under the chop gate is not offered.
                if (cooled(row.Id) || (!row.Designated && row.BelowChopGate(minGrowth)))
                {
                    held.Add(row.Id);
                }
            }
        }
        return held;
    }

    /// <summary>
    /// Holds, in held, every designated or undispatched source, and returns the resources with such work not on
    /// cooldown: those are busy.
    /// </summary>
    internal static HashSet<string> HoldWorked(Fact<IReadOnlyList<AcquisitionSource>> sources, HashSet<string> undispatched, HashSet<string> held)
    {
        var busyState = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (sources.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                if (row.Designated || undispatched.Contains(row.Id))
                {
                    busyState[row.Resource] = (busyState.TryGetValue(row.Resource, out var b) && b) || !held.Contains(row.Id);
                    held.Add(row.Id);
                }
            }
        }
        foreach (var thing in undispatched)
        {
            held.Add(thing);
        }
        return [.. busyState.Where(kv => kv.Value).Select(kv => kv.Key)];
    }

    /// <summary>Keeps the census's hunt rows.</summary>
    internal static Fact<IReadOnlyList<AcquisitionSource>> HuntRows(Fact<IReadOnlyList<AcquisitionSource>> sources)
    {
        if (!sources.TryGetValue(out var rows))
        {
            return sources;
        }
        return Known<IReadOnlyList<AcquisitionSource>>([.. rows.Where(row => row.Hunt)]);
    }

    /// <summary>
    /// Selects, from the acquisition census, the designated rows of one kind (hunt or not) the goal owns (mine)
    /// that no colonist has taken for at least the contract's deadline since native first saw the designation.
    /// No action pairing: a player's designation of the goal's kind stalls too. The planner withdraws each so
    /// the goal re-plans from another source. A taken row (reserved, or a colonist's job targets it) is never
    /// stalled. Harvests run on RoundsPolicy.AcquisitionProgress, hunts on HuntProgress. A contract without a
    /// deadline, or an unknown census, stalls nothing.
    /// </summary>
    internal static List<AcquisitionSource> StalledDesignations(Fact<IReadOnlyList<AcquisitionSource>> sources, bool hunt, Tick now, ProgressContract contract, Func<AcquisitionSource, bool> mine)
    {
        var stalled = new List<AcquisitionSource>();
        if (contract.Deadline <= 0 || !sources.TryGetValue(out var rows))
        {
            return stalled;
        }
        foreach (var row in rows)
        {
            if (row.Designated && !row.Taken && row.Hunt == hunt && mine(row) && contract.Expired(row.DesignatedTick, now))
            {
                stalled.Add(row);
            }
        }
        return stalled;
    }

    /// <summary>
    /// The one-action withdraw method of one stalled row. Its method id hashes the source and the designation's
    /// first-seen tick: a withdraw not yet read back is not filed twice, and a later re-designation of the same
    /// source rehashes.
    /// </summary>
    internal static (MethodId Method, PlanSpec Plan) StallWithdraw(AcquisitionSource row)
    {
        var method = new MethodId("withdraw-" + HashHex16($"{row.Id}/{row.Resource}/{row.Cell.X}/{row.Cell.Z}/{row.DesignatedTick}\n"));
        var value = new Acquisition(row.Id, row.Resource, row.Cell);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAcquisitionWithdrawAction(new ActionId($"{id}-0"), value);
        return (method, new PlanSpec(id, new PlanRevision(1), [action]));
    }

    /// <summary>
    /// Takes the census rows this step withdrew as stalled off native's pending total (nutrition for food,
    /// yield otherwise): a designation nobody took still counts there, and without this the re-plan would see
    /// its own stalled yield as covering the deficit and propose nothing.
    /// </summary>
    internal static Fact<double> WithoutStalled(Fact<double> pending, Fact<IReadOnlyList<AcquisitionSource>> sources, HashSet<string> stalled, bool food)
    {
        var known = sources.TryGetValue(out var rows);
        var pk = pending.TryGetValue(out var outstanding);
        if (!known || !pk || stalled.Count == 0)
        {
            return pending;
        }
        foreach (var row in rows!)
        {
            if (!stalled.Contains(row.Id) || !row.Designated)
            {
                continue;
            }
            outstanding -= food ? row.NutritionYield : row.Yield;
        }
        return Known(Math.Max(0, outstanding));
    }

    /// <summary>
    /// The admitted method's short why for Operation.intent: the food runway the plan budgets against, the
    /// animal a pest hunt targets, or the stock a resource method gathers.
    /// </summary>
    internal static string AcquisitionReason(bool food, bool pest, Fact<double> runway, IReadOnlyList<AcquisitionSource> selected)
    {
        if (selected.Count == 0)
        {
            return "";
        }
        if (pest)
        {
            return "pest " + selected[0].Definition;
        }
        if (food)
        {
            return runway.TryGetValue(out var days) ? $"food runway {days.ToString("F1", CultureInfo.InvariantCulture)}d" : "";
        }
        return selected[0].Resource + " low";
    }

    /// <summary>
    /// Says why the selection chose no source although the goal is owed: a pest hunt waits for a ranged hunter
    /// or has every pest already hunted, food in flight already covers the deficit, or no source stands.
    /// </summary>
    internal static Verdict NoAcquisition(ConcernId need, bool noHunter, Fact<double> deficit, Fact<double> pending)
    {
        if (need == PolicyApi.ClearPests)
        {
            return noHunter ? NoWorker("hunter") : BuildingReasonNoDeficit;
        }
        var owedKnown = deficit.TryGetValue(out var owed);
        var inFlightKnown = pending.TryGetValue(out var inFlight);
        if (owedKnown && inFlightKnown && inFlight >= owed)
        {
            return BuildingReasonExistingWork;
        }
        return AwaitingPlan("acquisition_source", need.Value);
    }

    /// <summary>
    /// Names what the source selection lacked when it could not choose: the source census, then (for goals sized
    /// by a deficit) the deficit and the pending yield. A census that is read but holds an unusable row reads as
    /// the census.
    /// </summary>
    internal static string UnreadAcquisitionFact(Fact<IReadOnlyList<AcquisitionSource>> sources, Fact<double> deficit, Fact<double> pending, bool withoutDeficit)
    {
        if (!sources.IsKnown)
        {
            return "acquisition_sources";
        }
        if (!withoutDeficit)
        {
            if (!deficit.IsKnown)
            {
                return "acquisition_deficit";
            }
            if (!pending.IsKnown)
            {
                return "pending_acquisition";
            }
        }
        return "acquisition_sources";
    }

    /// <summary>The first 16 bytes of the SHA-256 of the text, in lowercase hex (Go <c>%x</c> of <c>hash.Sum(nil)[:16]</c>).</summary>
    internal static string HashHex16(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))[..16]);
}

public sealed partial class Rounder
{
    /// <summary>The owned review reading an acquisition step plans on, its census without the sources a field plan reserves.</summary>
    internal (Identity Expected, ColonyProjection Projection) AcquisitionReading(CancellationToken call, ControlState state, RoundsReview review)
    {
        var p = Player;
        var plans = p.Journal.LoadPlans(call);
        var playerPlans = p.Journal.PlayerPlans(PlayerWorld(state.Snapshot), call);
        var definitions = RoundsProjectDefinitions(plans, state.Snapshot, playerPlans);
        var crops = SowableCrops(call, state.Snapshot);
        definitions = UniqueFieldDefinitions([.. definitions, .. crops]);
        var expected = StepScope(call, Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("acquisitionReading: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = ObserveOwned(call, Native, expected, claims, definitions);
        var projection = read.Projection;
        projection.Acquisition = WithoutFieldSources(projection.Acquisition, projection, plans, state.Snapshot);
        return (expected, projection);
    }
}
