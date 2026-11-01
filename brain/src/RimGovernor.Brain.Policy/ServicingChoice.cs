using System.Globalization;
using RimGovernor.Brain.GameFacts.Observations;
using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Policy;

// The maintenance choice (task 5.2 of redefine-habitat-requirements): each deficient home building's repair, breakdown fix,
// door-to-wall replacement, retirement and deferral compared by what each leaves unresolved, priced from the measured
// servicing activities (task 5.1). The home-upkeep family binds the winner as a claimed Method; nothing here orders work. The power
// family (task 5.3) compares a lost producer's breakdown fix, refuel and fuel acquisition chain through the same choice.

/// <summary>
/// What a target lacks: hit points below its maximum, a breakdown, a door the plan says should be a wall, or an empty fuel tank
/// (a refuelable out of fuel: its function is lost until fuel arrives).
/// </summary>
public enum ServicingDeficit { Condition, Breakdown, Swap, Fuel }

/// <summary>
/// One way to answer a target's deficits. Refuel carries stocked fuel to it; Acquire is the chain that first acquires the fuel the
/// colony holds none of (its first step) and then refuels; Retire leaves its removal to clearance; Defer is the baseline that does
/// nothing. A standing building a plan considers transitioning (task 8.1, <see cref="ServicingChoiceRules.Transition"/>) is
/// compared over Retain (the baseline: it stays as it stands), Repair, FixBreakdown, Replace, Relocate (uninstall and reinstall
/// elsewhere), Repurpose (a typed operation that changes its use), Store (uninstall to storage) and Remove (deconstruct).
/// </summary>
public enum ServicingChoiceKind { Repair, FixBreakdown, Replace, Refuel, Acquire, Retire, Defer, Retain, Relocate, Repurpose, Store, Remove }

/// <summary>
/// What an alternative leaves: whether the building's function is still lost (an unfixed breakdown or an empty tank), how many
/// deficits stay unresolved, the ticks a replacement leaves the cell open, the expected work ticks (a fix's divided by its success
/// chance; an acquisition chain's are unmeasured, positive infinity) and the expected inputs consumed.
/// </summary>
public sealed record ServicingEffect(bool FunctionLost, int Unresolved, double TransitionTicks, double Duration, double Inputs);

/// <summary>An alternative's standing: assessed with its effect, excluded by a named constraint, or not assessed for a named unread fact.</summary>
public abstract record ServicingStanding
{
    private ServicingStanding() { }

    public sealed record Assessed(ServicingEffect Effect) : ServicingStanding;

    public sealed record Excluded(string Reason) : ServicingStanding;

    /// <summary>A fact it needs is unread (rule 9): it is never chosen and names the fact.</summary>
    public sealed record Unassessed(string Fact) : ServicingStanding;

    /// <summary>The transition the plan already committed to (LayoutPlan.Transitions), read back instead of compared again.</summary>
    public sealed record Bound : ServicingStanding;

    /// <summary>
    /// A transition alternative priced in capability-days over the horizon (<see cref="ServicingChoiceRules.Transition"/>): its labor,
    /// material and service terms, and the days within the horizon it leaves the building's service lost.
    /// </summary>
    public sealed record Priced(TransitionPrice Price) : ServicingStanding;
}

/// <summary>
/// One compared alternative; <see cref="Work"/> is the measured work an assessed repair, fix, refuel or replacement binds, and
/// <see cref="Chain"/> the acquisition path an assessed <see cref="ServicingChoiceKind.Acquire"/> binds as its first step.
/// </summary>
public sealed record ServicingAlternative(ServicingChoiceKind Kind, string Pawn, ServicingStanding Standing)
{
    public ServicingWork? Work { get; init; }
    public ServicingAcquisition.Path? Chain { get; init; }
}

/// <summary>
/// How the colony gets a resource a servicing work consumes when it holds none: <see cref="Path"/>, the harvest sources an
/// acquisition designates (empty while the designations already made or held cover it), or <see cref="None"/> naming the state
/// (<c>fuel acquisition:&lt;def&gt;</c>) when no observed source yields it.
/// </summary>
public abstract record ServicingAcquisition
{
    private ServicingAcquisition() { }

    /// <param name="Resource">The resource acquired.</param>
    /// <param name="Deficit">The items the targets waiting on it need.</param>
    /// <param name="Pending">The yield of the sources already designated (native or ours), counted against the deficit.</param>
    /// <param name="Sources">The sources the acquisition designates now; empty when the pending designations cover the deficit.</param>
    public sealed record Path(Resource Resource, double Deficit, double Pending, EquatableArray<AcquisitionSource> Sources) : ServicingAcquisition;

    public sealed record None(string State) : ServicingAcquisition;
}

/// <summary>
/// One target's compared alternatives and the one chosen: the best assessed, Defer when no work is. <see cref="Underway"/> is the
/// servicing already at work on it (an open order or a running native job), which the choice continues instead of comparing.
/// </summary>
public sealed record ServicingDecision(string Target, EquatableArray<ServicingDeficit> Deficits, EquatableArray<ServicingAlternative> Alternatives, ServicingAlternative Chosen)
{
    public ServicingUnderway? Underway { get; init; }
}

/// <summary>
/// Servicing already at work on a target: <see cref="Issued"/> for an open order of the Need (its action not yet resolved),
/// otherwise a pawn's running native job (JobDefOf.Repair, FixBrokenDownBuilding or Refuel) on it.
/// </summary>
public sealed record ServicingUnderway(string Target, ServicingKind Kind, string Pawn, bool Issued);

/// <summary>A servicing order the native refused in the current world while the refusal budget refuses its Method: a fact the choice reads, never retried.</summary>
public sealed record RefusedServicing(string Target, ServicingKind Kind, string Pawn, string Reason);

/// <summary>The usable spawned stock of the component a breakdown fix consumes: counted, or unread with the fact named.</summary>
public abstract record ServicingComponents
{
    private ServicingComponents() { }

    public sealed record Counted(int Count) : ServicingComponents;

    public sealed record Unread(string Fact) : ServicingComponents;
}

/// <summary>
/// The measured servicing options of one native servicing read, joined to the canonical rows (<see cref="ServicingRules.Survey"/>);
/// <see cref="Components"/> is the stock of the component a fix consumes and <see cref="Truncated"/> whether the read's target
/// bound cut targets.
/// </summary>
public sealed record ServicingCensus(EquatableArray<ServicingOption> Options)
{
    public required ServicingComponents Components { get; init; }
    public bool Truncated { get; init; }

    /// <summary>The def a breakdown fix consumes (ServicingMechanics.Component), naming its acquisition state.</summary>
    public string Component { get; init; } = "";

    /// <summary>The read's usable stock of each resource a target's work consumes (ServicingActivityRead.Stock), by def.</summary>
    public IReadOnlyDictionary<string, int> Stock { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// What the maintenance choice reads: the deficient targets' canonical rows, most urgent first; the servicing census (null when
/// unread); the doors the clearance step swaps for walls; the targets the plan does not need (the clearance targets; null when
/// the clearance census is unread); the servicing underway and the refused orders, both re-derived each review from the journal
/// and the pawn census (no owner store).
/// </summary>
public sealed record ServicingRequest
{
    /// <summary>The deficits the choice answers: home upkeep's condition, breakdown and swap; power's breakdown and fuel.</summary>
    public IReadOnlySet<ServicingDeficit> Considered { get; init; } = new HashSet<ServicingDeficit> { ServicingDeficit.Condition, ServicingDeficit.Breakdown, ServicingDeficit.Swap };

    /// <summary>
    /// The acquisition path of each fuel the colony holds none of, by def: null when the request compares no acquisition
    /// chain (home upkeep), and a fix with no component in stock is then only excluded.
    /// </summary>
    public IReadOnlyDictionary<string, ServicingAcquisition>? Acquisitions { get; init; }

    public IReadOnlyList<BuildingRow> Targets { get; init; } = [];
    public ServicingCensus? Census { get; init; }
    public IReadOnlySet<string> Swaps { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string>? Unneeded { get; init; }
    public IReadOnlyList<ServicingUnderway> Underway { get; init; } = [];
    public IReadOnlyList<RefusedServicing> Refused { get; init; } = [];
}

public static partial class ServicingChoiceRules
{
    private const double Tolerance = 1e-9;

    /// <summary>The excluded reasons the choice itself adds to the measured options'.</summary>
    public const string PawnTaken = "pawn_taken", ComponentTaken = "component_taken", OutsideHomeArea = "outside_home_area",
        DoorPlanned = "door_planned", WallPlanned = "wall_planned", RefusedPrefix = "refused:", WorkInactive = "work_inactive",
        FuelTaken = "fuel_taken", FuelStocked = "fuel_stocked";

    /// <summary>The named states of a resource no observed path acquires: <c>fuel acquisition:&lt;def&gt;</c>, <c>component acquisition:&lt;def&gt;</c>.</summary>
    public const string FuelAcquisition = "fuel acquisition:", ComponentAcquisition = "component acquisition:";

    /// <summary>
    /// Decides every target in order. Servicing underway on a target is continued. Otherwise each alternative is assessed: a repair,
    /// fix or replacement per measured worker (a pawn another target already took, an order refused under the budget, a target
    /// outside the home area or a component another fix holds excludes it); retiring, zero-loss only when the plan does not need
    /// the building (otherwise unassessed, naming the service it would lose); deferring, the baseline. The best assessed by
    /// <see cref="Compare"/> wins; a chosen fix holds one component, a chosen repair, fix or refuel its pawn, and a chosen refuel the
    /// fuel items it carries (an issued refuel not yet running holds its items too), so two targets never count one stock twice.
    /// </summary>
    public static IReadOnlyList<ServicingDecision> Decide(ServicingRequest r)
    {
        var decisions = new List<ServicingDecision>();
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in r.Underway) pawns.Add(u.Pawn);
        // An issued fix not yet running holds the component it will fetch; a running one has reserved or carried it.
        var components = r.Census?.Components is ServicingComponents.Counted stock ? stock.Count - r.Underway.Count(u => u.Issued && u.Kind == ServicingKind.FixBreakdown) : (int?)null;
        var fuel = new Dictionary<string, int>(r.Census?.Stock ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        foreach (var u in r.Underway.Where(u => u.Issued && u.Kind == ServicingKind.Refuel))
        {
            if (r.Census?.Options.OfType<ServicingOption.Assessed>().Select(o => o.Work).OfType<RefuelWork>().FirstOrDefault(w => w.Target == u.Target && w.Pawn == u.Pawn) is { } issued)
                Take(fuel, issued);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in r.Targets)
        {
            var id = row.Building.Id;
            if (!seen.Add(id)) throw new DomainException($"servicing target {id} is listed twice");
            var deficits = DeficitsOf(row, r.Swaps, r.Considered);
            if (deficits.Count == 0) continue;
            if (r.Underway.FirstOrDefault(u => u.Target == id && Resolves(u.Kind, deficits)) is { } underway)
            {
                var continued = new ServicingAlternative(ChoiceOf(underway.Kind), underway.Pawn, new ServicingStanding.Assessed(Effect(Without(deficits, underway.Kind), 0, 0, 0)));
                decisions.Add(new(id, [.. deficits], [continued], continued) { Underway = underway });
                continue;
            }
            var alternatives = Alternatives(r, row, deficits, pawns, components, fuel);
            var chosen = alternatives.Where(a => a.Standing is ServicingStanding.Assessed).OrderBy(a => a, Comparer<ServicingAlternative>.Create(Compare)).First();
            if (chosen.Kind is ServicingChoiceKind.Repair or ServicingChoiceKind.FixBreakdown or ServicingChoiceKind.Refuel) pawns.Add(chosen.Pawn);
            if (chosen.Kind == ServicingChoiceKind.FixBreakdown) components--;
            if (chosen.Work is RefuelWork carried) Take(fuel, carried);
            decisions.Add(new(id, [.. deficits], [.. alternatives], chosen));
        }
        return decisions;
    }

    /// <summary>
    /// Whether the decision restores the target's function: every lost-function deficit (a breakdown, an empty tank) has an assessed
    /// alternative that resolves it (a fix; a refuel or an acquisition chain), or servicing underway resolving it.
    /// </summary>
    public static bool Restores(ServicingDecision d) =>
        d.Deficits.Where(x => x is ServicingDeficit.Breakdown or ServicingDeficit.Fuel).All(x =>
            (d.Underway is { } u && Resolves(u.Kind, [x]))
            || d.Alternatives.Any(a => a.Standing is ServicingStanding.Assessed && (x == ServicingDeficit.Breakdown
                ? a.Kind == ServicingChoiceKind.FixBreakdown
                : a.Kind is ServicingChoiceKind.Refuel or ServicingChoiceKind.Acquire)));

    private static void Take(Dictionary<string, int> fuel, RefuelWork work) =>
        fuel[work.Fuel.Value] = fuel.GetValueOrDefault(work.Fuel.Value) - work.Items;

    /// <summary>
    /// The building's function first (an alternative that leaves it broken down loses), then fewer unresolved deficits, then the
    /// shorter open transition, then the shorter expected work, then fewer inputs, then the kind and pawn: a total order over
    /// assessed alternatives. Deferring leaves every deficit, so any assessed work that resolves one wins over it.
    /// </summary>
    public static int Compare(ServicingAlternative a, ServicingAlternative b)
    {
        var x = ((ServicingStanding.Assessed)a.Standing).Effect;
        var y = ((ServicingStanding.Assessed)b.Standing).Effect;
        if (x.FunctionLost != y.FunctionLost) return x.FunctionLost.CompareTo(y.FunctionLost);
        if (x.Unresolved != y.Unresolved) return x.Unresolved.CompareTo(y.Unresolved);
        if (Math.Abs(x.TransitionTicks - y.TransitionTicks) > Tolerance) return x.TransitionTicks.CompareTo(y.TransitionTicks);
        if (Math.Abs(x.Duration - y.Duration) > Tolerance) return x.Duration.CompareTo(y.Duration);
        if (Math.Abs(x.Inputs - y.Inputs) > Tolerance) return x.Inputs.CompareTo(y.Inputs);
        if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
        return string.CompareOrdinal(a.Pawn, b.Pawn);
    }

    /// <summary>The target's deficits from its canonical row and the clearance step's swaps: home upkeep's condition, breakdown and swap.</summary>
    public static List<ServicingDeficit> DeficitsOf(BuildingRow row, IReadOnlySet<string> swaps) =>
        DeficitsOf(row, swaps, new ServicingRequest().Considered);

    /// <summary>The target's <paramref name="considered"/> deficits from its canonical row and the clearance step's swaps.</summary>
    public static List<ServicingDeficit> DeficitsOf(BuildingRow row, IReadOnlySet<string> swaps, IReadOnlySet<ServicingDeficit> considered)
    {
        var deficits = new List<ServicingDeficit>();
        if (row.HitPoints is BuildingHitPoints.Tracked hp && hp.Current < hp.Maximum) deficits.Add(ServicingDeficit.Condition);
        if (row.Service.BrokenDown) deficits.Add(ServicingDeficit.Breakdown);
        if (swaps.Contains(row.Building.Id)) deficits.Add(ServicingDeficit.Swap);
        if (row.Service.Fuel is BuildingFuel.Refuelable { OutOfFuel: true }) deficits.Add(ServicingDeficit.Fuel);
        return [.. deficits.Where(considered.Contains)];
    }

    /// <summary>One clause per decision for a hold's detail: the target, its choice, and for a deferral why no work was chosen.</summary>
    public static string Describe(ServicingDecision d)
    {
        if (d.Underway is { } u) return $"{d.Target}: {u.Kind} by {u.Pawn} underway";
        if (d.Chosen.Kind == ServicingChoiceKind.Retire) return $"{d.Target}: retired by clearance";
        if (d.Chosen.Work is { } work) return $"{d.Target}: {ServicingRules.DescribeWork(work)}";
        if (d.Chosen.Chain is { } chain) return string.Create(CultureInfo.InvariantCulture,
            $"{d.Target}: acquire {chain.Resource.Value} ({chain.Deficit:0.##} needed, {chain.Pending:0.##} designated, {chain.Sources.Count} sources to designate), then refuel");
        var why = d.Alternatives.Where(a => a.Kind != ServicingChoiceKind.Defer).Select(a => a.Standing switch
        {
            ServicingStanding.Excluded e => $"{a.Kind}{By(a.Pawn)} excluded {e.Reason}",
            ServicingStanding.Unassessed n => $"{a.Kind}{By(a.Pawn)} unassessed {n.Fact}",
            ServicingStanding.Assessed => $"{a.Kind}{By(a.Pawn)} assessed",
            var other => throw new DomainException($"unknown standing {other}"),
        });
        return $"{d.Target} ({string.Join("+", d.Deficits)}): deferred ({string.Join(", ", why)})";
    }

    private static string By(string pawn) => pawn.Length == 0 ? "" : " by " + pawn;

    private static List<ServicingAlternative> Alternatives(ServicingRequest r, BuildingRow row, List<ServicingDeficit> deficits, HashSet<string> pawns, int? components,
        Dictionary<string, int> fuel)
    {
        var id = row.Building.Id;
        var output = new List<ServicingAlternative>();
        if (deficits.Contains(ServicingDeficit.Condition)) output.AddRange(Work(r, row, ServicingKind.Repair, deficits, pawns, components, fuel));
        if (deficits.Contains(ServicingDeficit.Breakdown))
        {
            output.AddRange(Work(r, row, ServicingKind.FixBreakdown, deficits, pawns, components, fuel));
            // No component in stock and no observed path that acquires one: the fix waits on a named state, not a timer.
            if (r.Acquisitions is not null && r.Census is { Components: ServicingComponents.Counted { Count: <= 0 } } census)
                output.Add(new(ServicingChoiceKind.Acquire, "", new ServicingStanding.Unassessed(ComponentAcquisition + census.Component)));
        }
        if (deficits.Contains(ServicingDeficit.Fuel))
        {
            output.AddRange(Work(r, row, ServicingKind.Refuel, deficits, pawns, components, fuel));
            if (r.Acquisitions is not null) output.Add(Chain(r, row, deficits, fuel));
        }
        if (deficits.Contains(ServicingDeficit.Swap)) output.AddRange(Work(r, row, ServicingKind.Replace, deficits, pawns, components, fuel));
        else if (r.Considered.Contains(ServicingDeficit.Swap) && r.Census?.Options.Any(o => o.Target == id && o.Kind == ServicingKind.Replace) == true)
            output.Add(new(ServicingChoiceKind.Replace, "", new ServicingStanding.Excluded(DoorPlanned)));
        output.Add(new(ServicingChoiceKind.Retire, "", Retire(r, row, deficits)));
        output.Add(new(ServicingChoiceKind.Defer, "", new ServicingStanding.Assessed(Effect(deficits, 0, 0, 0))));
        return output;
    }

    /// <summary>
    /// The acquire-then-refuel chain of an empty target: excluded while any of its fuels is still in stock (a refuel carries it),
    /// assessed with the first fuel's acquisition path (its duration unmeasured: the harvest is native-led), or unassessed naming
    /// <c>fuel acquisition:&lt;def&gt;</c> when no observed source yields any of its fuels.
    /// </summary>
    private static ServicingAlternative Chain(ServicingRequest r, BuildingRow row, List<ServicingDeficit> deficits, Dictionary<string, int> fuel)
    {
        var defs = row.FuelDefinitions;
        if (defs.Count == 0) return new(ServicingChoiceKind.Acquire, "", new ServicingStanding.Unassessed("fuel def:" + row.Building.DefName));
        if (defs.Any(d => fuel.GetValueOrDefault(d) > 0)) return new(ServicingChoiceKind.Acquire, "", new ServicingStanding.Excluded(FuelStocked));
        foreach (var def in defs)
        {
            if (r.Acquisitions!.GetValueOrDefault(def) is ServicingAcquisition.Path path)
                return new(ServicingChoiceKind.Acquire, "", new ServicingStanding.Assessed(Effect(Without(deficits, ServicingKind.Refuel), 0, double.PositiveInfinity, 0))) { Chain = path };
        }
        return new(ServicingChoiceKind.Acquire, "", new ServicingStanding.Unassessed(r.Acquisitions!.GetValueOrDefault(defs[0]) is ServicingAcquisition.None none
            ? none.State : FuelAcquisition + defs[0]));
    }

    /// <summary>Retiring: zero-loss when the plan does not need the building; excluded for a door the plan walls; otherwise unassessed for the service it would lose.</summary>
    private static ServicingStanding Retire(ServicingRequest r, BuildingRow row, List<ServicingDeficit> deficits)
    {
        if (deficits.Contains(ServicingDeficit.Swap)) return new ServicingStanding.Excluded(WallPlanned);
        if (r.Unneeded is not { } unneeded) return new ServicingStanding.Unassessed("clearance census");
        if (unneeded.Contains(row.Building.Id)) return new ServicingStanding.Assessed(new(false, 0, 0, 0, 0));
        return new ServicingStanding.Unassessed(ServicingRules.FunctionOf(row) switch
        {
            ServiceLoss.PowerOutput p => string.Create(CultureInfo.InvariantCulture, $"power output value:{row.Building.DefName} ({p.Watts:0} W on {p.Network})"),
            ServiceLoss.Unmodelled u => u.State,
            var other => throw new DomainException($"unexpected function loss {other}"),
        });
    }

    private static IEnumerable<ServicingAlternative> Work(ServicingRequest r, BuildingRow row, ServicingKind kind, List<ServicingDeficit> deficits, HashSet<string> pawns, int? components,
        Dictionary<string, int> fuel)
    {
        var id = row.Building.Id;
        var choice = ChoiceOf(kind);
        if (r.Census is not { } census)
        {
            yield return new(choice, "", new ServicingStanding.Unassessed("servicing read"));
            yield break;
        }
        var options = census.Options.Where(o => o.Target == id && o.Kind == kind).ToArray();
        if (options.Length == 0)
        {
            yield return new(choice, "", new ServicingStanding.Unassessed(census.Truncated ? ServicingRules.Truncated : "servicing target:" + id));
            yield break;
        }
        foreach (var option in options)
        {
            if (option is ServicingOption.Excluded excluded) yield return new(choice, option.Pawn, new ServicingStanding.Excluded(excluded.Reason));
            else if (option is ServicingOption.Unassessed unassessed) yield return new(choice, option.Pawn, new ServicingStanding.Unassessed(unassessed.Fact));
            else
            {
                var work = ((ServicingOption.Assessed)option).Work;
                yield return new(choice, option.Pawn, Standing(r, census, row, work, deficits, pawns, components, fuel)) { Work = work };
            }
        }
    }

    private static ServicingStanding Standing(ServicingRequest r, ServicingCensus census, BuildingRow row, ServicingWork work, List<ServicingDeficit> deficits, HashSet<string> pawns, int? components,
        Dictionary<string, int> fuel)
    {
        if (r.Refused.FirstOrDefault(x => x.Target == work.Target && x.Kind == work.Kind && x.Pawn == work.Pawn) is { } refused)
            return new ServicingStanding.Excluded(RefusedPrefix + refused.Reason);
        // A replacement is a designation native-led labor works: it takes no pawn, and a builder whose work types are inactive
        // never takes it. A repair or fix is a player-forced order (TryTakeOrderedJobPrioritizedWork), which the native gives
        // whatever the pawn's work priorities, so its WorkActive does not exclude it.
        if (work.Kind == ServicingKind.Replace && !work.WorkActive) return new ServicingStanding.Excluded(WorkInactive);
        if (work.Kind != ServicingKind.Replace && pawns.Contains(work.Pawn)) return new ServicingStanding.Excluded(PawnTaken);
        // WorkGiver_Refuel takes a target anywhere; only the repair and fix givers require the home area.
        if (!work.InHomeArea && work.Kind != ServicingKind.Refuel) return new ServicingStanding.Excluded(OutsideHomeArea);
        var left = Without(deficits, work.Kind);
        switch (work)
        {
            case RepairWork repair:
                if (repair.ServiceLoss is ServiceLoss.Condition loss && loss.MissingHitPoints != repair.HitPoints)
                    throw new DomainException($"repair of {repair.Target} restores {repair.HitPoints} hit points, its loss is {loss.MissingHitPoints}");
                return new ServicingStanding.Assessed(Effect(left, 0, repair.Duration, 0));
            case FixBreakdownWork fix:
                if (census.Components is ServicingComponents.Unread unread) return new ServicingStanding.Unassessed(unread.Fact);
                if (components <= 0) return new ServicingStanding.Excluded(census.Components is ServicingComponents.Counted { Count: > 0 } ? ComponentTaken : ServicingRules.NoStock);
                if (fix.SuccessChance <= 0) return new ServicingStanding.Excluded(ServicingRules.ZeroRate);
                return new ServicingStanding.Assessed(Effect(left, 0, fix.Duration / fix.SuccessChance, fix.Inputs.Sum(i => i.Count) / fix.SuccessChance));
            case RefuelWork refuel:
                if (fuel.GetValueOrDefault(refuel.Fuel.Value) < refuel.Items) return new ServicingStanding.Excluded(FuelTaken);
                return new ServicingStanding.Assessed(Effect(left, 0, refuel.Duration, refuel.Items));
            case ReplacementWork swap:
                var transition = swap.ServiceLoss is ServiceLoss.Enclosure e ? e.Ticks : 0;
                return new ServicingStanding.Assessed(Effect(left, transition, swap.Duration, swap.NetInputs.Sum(i => Math.Max(0, i.Count))));
        }
        throw new DomainException($"no maintenance choice for {work.Kind} of {row.Building.Id}");
    }

    private static ServicingEffect Effect(List<ServicingDeficit> left, double transition, double duration, double inputs) =>
        new(left.Contains(ServicingDeficit.Breakdown) || left.Contains(ServicingDeficit.Fuel), left.Count, transition, duration, inputs);

    private static List<ServicingDeficit> Without(List<ServicingDeficit> deficits, ServicingKind kind) => kind switch
    {
        ServicingKind.Repair => [.. deficits.Where(d => d != ServicingDeficit.Condition)],
        ServicingKind.FixBreakdown => [.. deficits.Where(d => d != ServicingDeficit.Breakdown)],
        ServicingKind.Refuel => [.. deficits.Where(d => d != ServicingDeficit.Fuel)],
        ServicingKind.Replace => [],
        var other => throw new DomainException($"servicing kind {other} resolves no maintenance deficit"),
    };

    private static bool Resolves(ServicingKind kind, List<ServicingDeficit> deficits) =>
        (kind == ServicingKind.Repair && deficits.Contains(ServicingDeficit.Condition))
        || (kind == ServicingKind.FixBreakdown && deficits.Contains(ServicingDeficit.Breakdown))
        || (kind == ServicingKind.Refuel && deficits.Contains(ServicingDeficit.Fuel));

    private static ServicingChoiceKind ChoiceOf(ServicingKind kind) => kind switch
    {
        ServicingKind.Repair => ServicingChoiceKind.Repair,
        ServicingKind.FixBreakdown => ServicingChoiceKind.FixBreakdown,
        ServicingKind.Replace => ServicingChoiceKind.Replace,
        ServicingKind.Refuel => ServicingChoiceKind.Refuel,
        var other => throw new DomainException($"servicing kind {other} is not a maintenance choice"),
    };
}
