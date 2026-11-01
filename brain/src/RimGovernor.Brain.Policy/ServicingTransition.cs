using RimGovernor.Brain.GameFacts.Observations;
using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Policy;

// The transition choice (task 8.1 of redefine-habitat-requirements): a standing building that a plan omits, retires, replaces or
// finds spare is compared over retaining, repairing, fixing, replacing, relocating, repurposing, storing and removing it, each
// priced in capability-days from the native transition mechanics (ServicingActivityRead.Transitions) and the owning model's
// service term. The proposing paths (retired ground, planned-room reconcile, a host's spare, a solo bedroom's spare bed) only
// propose targets; this comparator decides, and a removal it chooses is admitted only while what the building provides is observed
// served by a replacement (TransitionBridgingRules, task 8.3): omission alone never takes a building down.

/// <summary>Why a plan considers transitioning a standing building.</summary>
public enum TransitionCause { PlannedGround, StaleInterior, RetiredGround, HostRetirement, SpareBed }

/// <summary>
/// The service the building gives as its owning model values it: <see cref="Assessed"/> capability-days a day from
/// <see cref="Assessed.StartDay"/> (days from now; a dated arrival's need starts then), or <see cref="Unassessed"/> naming the
/// unread fact. An unassessed service is never traded away: an alternative losing more of it than another is unassessed too.
/// </summary>
public abstract record TransitionService
{
    private TransitionService() { }

    public sealed record Assessed(double PerDay, double StartDay) : TransitionService;

    public sealed record Unassessed(string Fact) : TransitionService;
}

/// <summary>
/// What building a planned piece new costs: <see cref="Priced"/> its capability-days (LayoutClusterRules.PiecePrice of its work and
/// cost list in its ordinary build stuff), or <see cref="Unread"/> naming the catalog row, stat or material value left unread.
/// </summary>
public abstract record TransitionBuild
{
    private TransitionBuild() { }

    public sealed record Priced(double Days) : TransitionBuild;

    public sealed record Unread(string Fact) : TransitionBuild;
}

/// <summary>
/// An unfilled planned slot of the building's def it could be reinstalled at (ReconcileRules.RelocationSlots proposes it):
/// <see cref="Haul"/> the loaded carry from the building to the slot and <see cref="Avoided"/> what building that piece new
/// there would cost. The slot's service is the same whether the piece is moved in or built new, so moving it competes with
/// building it on the build price alone; the install itself is the delivery (Blueprint_Install's WorkTotal is 150, but the
/// native installs a minified thing when it is carried onto its blueprint), so reinstalling adds the haul only
/// (kept-constants.md, "Mirrored game literals: transitions").
/// </summary>
public sealed record TransitionDestination(TransitionLeg Haul, TransitionBuild Avoided)
{
    /// <summary>The slot's minimum cell: what a bound Relocate is checked against (TransitionBindingRules.Holds).</summary>
    public Cell Slot { get; init; }

    /// <summary>
    /// The planned piece the slot is (its anchor and rotation): where a chosen Relocate's reinstall blueprint is placed
    /// (OpKind.Reinstall). Null leaves the Relocate without a placement, so it is not executed.
    /// </summary>
    public WantedPiece? Piece { get; init; }
}

/// <summary>
/// One standing building proposed for a transition. <see cref="RetainExcluded"/> names why it cannot stay as it stands (its
/// footprint is planned, its owning model replaced it); <see cref="Service"/> is the owning model's service term (null:
/// unassessed, naming <c>service:&lt;def&gt;</c>); <see cref="Destination"/> a planned cell it could be relocated to.
/// </summary>
public sealed record TransitionCandidate(string Target, string Definition, TransitionCause Cause)
{
    public string? RetainExcluded { get; init; }
    public TransitionService? Service { get; init; }
    public TransitionDestination? Destination { get; init; }

    /// <summary>The replacement whose observed service a removal of the building depends on, named only while it serves (a host spare's).</summary>
    public ReplacementDependency? Dependency { get; init; }
}

/// <summary>
/// The facts every transition is priced from: the native read (null while unread), the material values per unit (null while
/// unread), the horizon, and the servicing options of the same read (repairs and fixes, ServicingRules.Survey).
/// </summary>
public sealed record TransitionFacts
{
    public ServicingActivityRead? Read { get; init; }
    public IReadOnlyDictionary<Resource, ConstructionMaterialValue>? Values { get; init; }
    public double HorizonDays { get; init; } = ColonyModelPolicy.DefaultHorizonDays;
    public IReadOnlyList<ServicingOption> Servicing { get; init; } = [];

    /// <summary>A standing building's service as its occupancy and room contribution read it (ServicingChoiceRules.StandingServiceOf); null leaves it unassessed.</summary>
    public Func<ClearanceTarget, TransitionService>? Standing { get; init; }

    /// <summary>What building a planned piece new costs, the price a relocation into its slot avoids; null leaves it unread.</summary>
    public Func<WantedPiece, TransitionBuild>? BuildPrice { get; init; }

    /// <summary>The transitions the plan committed to (LayoutPlan.Transitions): read back by <see cref="ServicingChoiceRules.Choose"/> while they hold.</summary>
    public IReadOnlyList<PlannedTransition> Planned { get; init; } = [];

    /// <summary>
    /// The bridging provision taking a standing building (its id and definition) down would leave without an observed serving replacement
    /// (TransitionBridgingRules.UnservedOf: <c>rest:&lt;pawn&gt;</c>, <c>power:&lt;consumer&gt;</c>, <c>pen:&lt;herd&gt;</c>,
    /// <c>defense:&lt;tier&gt;</c>), null when nothing it provides is left unserved; null itself reads no dependency.
    /// </summary>
    public Func<string, string, string?>? Unserved { get; init; }
}

/// <summary>
/// An alternative's price: <see cref="Labor"/> its work and walks, <see cref="Material"/> what it consumes or destroys less what it
/// credits, <see cref="Service"/> the service it loses (zero while the service is unassessed and the compared alternatives lose the
/// same), all capability-days.
/// </summary>
public sealed record TransitionPrice(double Labor, double Material, double Service)
{
    public double Total => Labor + Material + Service;
}

/// <summary>One building's compared transition alternatives and the cheapest; null when none could be priced (a named hold).</summary>
public sealed record TransitionDecision(string Target, string Definition, TransitionCause Cause, EquatableArray<ServicingAlternative> Alternatives, ServicingAlternative? Chosen)
{
    /// <summary>
    /// Why the chosen removal waits: <c>replacement not serving:&lt;dependant&gt;</c> while what the building provides is not observed
    /// served by a replacement (TransitionFacts.Unserved); null when the choice keeps the building or its removal is admitted.
    /// </summary>
    public string? Gate { get; init; }

    /// <summary>Whether the chosen alternative takes the building off its cell (Store, Remove or Relocate) with its replacement observed serving.</summary>
    public bool Admitted => Gate is null && Chosen?.Kind is ServicingChoiceKind.Store or ServicingChoiceKind.Remove or ServicingChoiceKind.Relocate;

    /// <summary>The dependency state the decision row names: <c>replacement serving</c>, or its <see cref="Gate"/>.</summary>
    public string DependencyState => Gate ?? ServicingChoiceRules.ReplacementServing;

    /// <summary>Whether the chosen removal packs the building (uninstalls it) rather than deconstructing it.</summary>
    public bool Uninstalls => Chosen?.Kind is ServicingChoiceKind.Store or ServicingChoiceKind.Relocate;

    /// <summary>The transition this decision binds into the plan (TransitionBindingRules.PlannedOf); null when it takes nothing off its cell.</summary>
    public PlannedTransition? Planned { get; init; }

    /// <summary>The named hold: a gated removal's <see cref="Gate"/>, or while nothing could be priced <c>transition:&lt;target&gt; (&lt;kind&gt; &lt;standing&gt;, ...)</c>.</summary>
    public string? Hold => Gate ?? (Chosen is not null ? null : "transition:" + Target + " " + Definition + " " + Cause + " (" + string.Join(", ", Alternatives.Select(a => a.Standing switch
    {
        ServicingStanding.Excluded e => $"{a.Kind} excluded {e.Reason}",
        ServicingStanding.Unassessed n => $"{a.Kind} unassessed {n.Fact}",
        ServicingStanding.Priced or ServicingStanding.Assessed => $"{a.Kind} assessed",
        var other => throw new InvalidOperationException("transition standing: " + other),
    })) + ")");
}

public static partial class ServicingChoiceRules
{
    /// <summary>The excluded reasons and unread facts the transition choice names.</summary>
    public const string NotMinifiable = "not_minifiable", NoAcceptingStorage = "no_accepting_storage", NoDestination = "no_destination",
        NoTypedOp = "no_typed_op", NotDeconstructible = "not_deconstructible",
        TransitionRead = "transition read", TransitionTruncated = "transition_targets_truncated", MaterialValues = "material values";

    /// <summary>The dependency states a transition row names: its replacement observed serving, or the gate's prefix before the unserved dependant.</summary>
    public const string ReplacementServing = "replacement serving", ReplacementNotServing = "replacement not serving:";

    // One alternative before its service term: a standing already set (excluded or unassessed), or its labor and material with
    // the interval [From, To] (days from now) it leaves the building's service lost.
    private readonly record struct Raw(ServicingChoiceKind Kind, string Pawn, ServicingStanding? Standing, double Labor, double Material, double From, double To);

    /// <summary>
    /// The transition a proposing path acts on: the plan's bound transition for the target while it still answers the proposal
    /// (TransitionBindingRules.Holds) and what the building provides is still observed served by a replacement, read back without
    /// comparing; otherwise the comparison (<see cref="Transition"/>) with the transition it binds (TransitionDecision.Planned). A
    /// chosen removal whose replacement is not observed serving on these facts (TransitionFacts.Unserved) is gated, named
    /// <c>replacement not serving:&lt;dependant&gt;</c>, and binds nothing: a replacement lost before the old service is removed
    /// drops the binding, so the path's unissued removal is stale and cancelled and the building stays. Omission is never a cause.
    /// </summary>
    public static TransitionDecision Choose(TransitionCandidate c, TransitionFacts? f)
    {
        var unserved = f?.Unserved?.Invoke(c.Target, c.Definition);
        if (unserved is null && f?.Planned.FirstOrDefault(t => t.Target == c.Target) is { } bound && TransitionBindingRules.Holds(bound, c))
        {
            return new TransitionDecision(c.Target, c.Definition, c.Cause, [], new ServicingAlternative(bound.Kind, "", new ServicingStanding.Bound())) { Planned = bound };
        }
        var d = Transition(c, f);
        if (unserved is not null && d.Chosen?.Kind is ServicingChoiceKind.Store or ServicingChoiceKind.Remove or ServicingChoiceKind.Relocate)
        {
            d = d with { Gate = ReplacementNotServing + unserved };
        }
        return d with { Planned = TransitionBindingRules.PlannedOf(c, d) };
    }

    /// <summary>
    /// The transition choice. Each alternative is priced in capability-days over the horizon H: its labor (the worker's walk and the
    /// driver's work at ConstructionSpeed x WorkPerSpeed, over a day's ticks), its material (consumed inputs; Remove destroys the
    /// building's CostListAdjusted value and credits the CostListAdjusted x resourcesFractionWhenDeconstructed it recovers; Store and
    /// Relocate keep the building whole) and the service it loses (the service term over the days within H it leaves the function
    /// lost, from the service's start). Retain is the baseline and costs nothing unless the building is broken down (it then loses
    /// its service over H); Repair restores hit points, which carry no function (StatPart_Health prices only), so damage alone never
    /// beats Retain; FixBreakdown loses service only for its expected work; Relocate needs a minifiable building and a destination;
    /// Repurpose needs a typed operation (none is modelled); Store needs a minifiable building and storage that accepts it; Remove
    /// needs a deconstructible one. A fact an alternative needs that is unread makes it unassessed; with the service unassessed, an
    /// alternative losing more service days than the least-losing measured one is unassessed too, so an unassessed service is never
    /// beaten by Remove. The cheapest priced alternative wins, Retain on a tie.
    /// </summary>
    public static TransitionDecision Transition(TransitionCandidate c, TransitionFacts? f)
    {
        var raw = new List<Raw>();
        void Excluded(ServicingChoiceKind kind, string reason) => raw.Add(new(kind, "", new ServicingStanding.Excluded(reason), 0, 0, 0, 0));
        void Unread(ServicingChoiceKind kind, string fact) => raw.Add(new(kind, "", new ServicingStanding.Unassessed(fact), 0, 0, 0, 0));
        var horizon = f?.HorizonDays ?? ColonyModelPolicy.DefaultHorizonDays;
        var mechanics = f?.Read?.Transitions.FirstOrDefault(t => t.Id == c.Target);
        var missing = f?.Read is not { } read ? TransitionRead
            : mechanics is not null ? null
            : read.Findings.Contains(TransitionTruncated) ? TransitionTruncated : "transition target:" + c.Target;
        // Retain: the building stays as it stands; a breakdown leaves its service lost over the horizon.
        if (c.RetainExcluded is { } kept) Excluded(ServicingChoiceKind.Retain, kept);
        else if (mechanics is null) Unread(ServicingChoiceKind.Retain, missing!);
        else raw.Add(new(ServicingChoiceKind.Retain, "", null, 0, 0, 0, mechanics.BrokenDown ? horizon : 0));
        if (mechanics is null)
        {
            // Without the building's mechanics nothing but staying is known: staying needs no fact while the building is not broken.
            if (c.RetainExcluded is null) raw[0] = new(ServicingChoiceKind.Retain, "", null, 0, 0, 0, 0);
            foreach (var kind in new[] { ServicingChoiceKind.Relocate, ServicingChoiceKind.Store, ServicingChoiceKind.Remove }) Unread(kind, missing!);
            Excluded(ServicingChoiceKind.Repurpose, NoTypedOp);
            return Decide(c, horizon, raw);
        }
        var m = mechanics;
        var rate = (m.Worker?.Ability as ServicingAbility.Construction)?.Speed * f!.Read!.Mechanics.WorkPerSpeed;
        // Why the worker cannot do the work, or null: no worker, an unread rate (unassessed), a zero rate, an inactive work type.
        ServicingStanding? Blocked(bool active)
        {
            if (m.Worker is not { } w) return new ServicingStanding.Excluded(ServicingRules.NoWorker);
            if (w.Ability is ServicingAbility.Unread u) return new ServicingStanding.Unassessed(u.Fact);
            if (rate is not > 0) return new ServicingStanding.Excluded(ServicingRules.ZeroRate);
            return active ? null : new ServicingStanding.Excluded(WorkInactive);
        }
        // Repair, fix and replace: the servicing read's measured work on the same building.
        Servicing(c, f, m, horizon, raw);
        // Relocate: uninstall, carry to the planned cell and reinstall there (the install itself is the delivery).
        if (!m.Minifiable) Excluded(ServicingChoiceKind.Relocate, NotMinifiable);
        else if (c.Destination is not { } destination) Excluded(ServicingChoiceKind.Relocate, NoDestination);
        else if (Blocked(m.Worker?.UninstallActive == true) is { } relocateBlock) raw.Add(new(ServicingChoiceKind.Relocate, "", relocateBlock, 0, 0, 0, 0));
        else if (destination.Haul is TransitionLeg.Unread { Fact: var legFact }) Unread(ServicingChoiceKind.Relocate, "relocation leg:" + c.Target + ":" + legFact);
        else if (destination.Avoided is TransitionBuild.Unread { Fact: var buildFact }) Unread(ServicingChoiceKind.Relocate, buildFact);
        else if (destination.Haul is not TransitionLeg.Walked { Ticks: var haul } || destination.Avoided is not TransitionBuild.Priced { Days: var avoided })
            throw new InvalidOperationException("transition destination: " + destination);
        else
        {
            var days = (m.Worker!.ApproachTicks + m.UninstallWork / rate!.Value + haul) / GameTime.TicksPerDay;
            raw.Add(new(ServicingChoiceKind.Relocate, m.Worker.Pawn, null, days, -avoided, 0, days));
        }
        Excluded(ServicingChoiceKind.Repurpose, NoTypedOp);
        // Store: uninstall and haul the minified building to storage that accepts it; the building is kept whole.
        if (!m.Minifiable) Excluded(ServicingChoiceKind.Store, NotMinifiable);
        else if (m.Storage is TransitionStorage.None none) Excluded(ServicingChoiceKind.Store, none.Reason);
        else if (Blocked(m.Worker?.UninstallActive == true) is { } storeBlock) raw.Add(new(ServicingChoiceKind.Store, "", storeBlock, 0, 0, 0, 0));
        else if (m.Worker!.StoreLeg is TransitionLeg.Unread leg) Unread(ServicingChoiceKind.Store, "store leg:" + c.Target + ":" + leg.Fact);
        else if (m.Worker.StoreLeg is not TransitionLeg.Walked { Ticks: var store }) throw new InvalidOperationException("transition store leg: " + m.Worker.StoreLeg);
        else raw.Add(new(ServicingChoiceKind.Store, m.Worker.Pawn, null, (m.Worker.ApproachTicks + m.UninstallWork / rate!.Value + store) / GameTime.TicksPerDay, 0, 0, horizon));
        // Remove: deconstruct, destroying the building's value and recovering the deconstruct fraction of its cost list.
        if (!m.Deconstructible) Excluded(ServicingChoiceKind.Remove, NotDeconstructible);
        else if (m.Removal is ServicingRemoval.Unread unread) Unread(ServicingChoiceKind.Remove, unread.Fact);
        else if (Blocked(m.Worker?.DeconstructActive == true) is { } removeBlock) raw.Add(new(ServicingChoiceKind.Remove, "", removeBlock, 0, 0, 0, 0));
        else
        {
            // A building with no cost list destroys and recovers nothing: only a priced cost list needs the material values.
            var removal = (ServicingRemoval.Read)m.Removal;
            var values = f.Values ?? new Dictionary<Resource, ConstructionMaterialValue>();
            if (removal.Costs.FirstOrDefault(cost => !values.ContainsKey(new(cost.Resource))) is { } unpriced)
                Unread(ServicingChoiceKind.Remove, f.Values is null ? MaterialValues : "material value:" + unpriced.Resource);
            else
                raw.Add(new(ServicingChoiceKind.Remove, m.Worker!.Pawn, null, (m.Worker.ApproachTicks + removal.DeconstructWork / rate!.Value) / GameTime.TicksPerDay,
                    RemovalMaterial(removal, values), 0, horizon));
        }
        return Decide(c, horizon, raw);
    }

    /// <summary>
    /// The material term of deconstructing: the building's CostListAdjusted value destroyed less the CostListAdjusted x
    /// resourcesFractionWhenDeconstructed recovered (GenLeaving, each count at most itself), in capability-days.
    /// </summary>
    public static double RemovalMaterial(ServicingRemoval.Read removal, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values) =>
        removal.Costs.Sum(cost => (cost.Count - Math.Min(cost.Count * removal.RefundFraction, cost.Count)) * values[new(cost.Resource)].PerUnit);

    // The servicing read's repair, fix and replacement of the building, as transition alternatives.
    private static void Servicing(TransitionCandidate c, TransitionFacts f, TransitionMechanics m, double horizon, List<Raw> raw)
    {
        var options = f.Servicing.Where(o => o.Target == c.Target).ToArray();
        foreach (var (kind, choice) in new[] { (ServicingKind.Repair, ServicingChoiceKind.Repair), (ServicingKind.FixBreakdown, ServicingChoiceKind.FixBreakdown), (ServicingKind.Replace, ServicingChoiceKind.Replace) })
        {
            var mine = options.Where(o => o.Kind == kind).ToArray();
            if (mine.Length == 0)
            {
                // A breakdown is the building's: with no measured fix it is unassessed, never silently left broken.
                if (kind == ServicingKind.FixBreakdown && m.BrokenDown) raw.Add(new(choice, "", new ServicingStanding.Unassessed("servicing target:" + c.Target), 0, 0, 0, 0));
                continue;
            }
            var best = mine.OfType<ServicingOption.Assessed>().Select(o => o.Work).Where(w => w.Kind != ServicingKind.Replace || w.WorkActive)
                .OrderBy(w => w.Duration).ThenBy(w => w.Pawn, StringComparer.Ordinal).FirstOrDefault();
            if (best is null)
            {
                raw.Add(mine[0] switch
                {
                    ServicingOption.Excluded e => new(choice, e.Pawn, new ServicingStanding.Excluded(e.Reason), 0, 0, 0, 0),
                    ServicingOption.Unassessed u => new(choice, u.Pawn, new ServicingStanding.Unassessed(u.Fact), 0, 0, 0, 0),
                    var other => new(choice, other.Pawn, new ServicingStanding.Excluded(WorkInactive), 0, 0, 0, 0),
                });
                continue;
            }
            switch (best)
            {
                case RepairWork repair:
                    // Hit points carry no function: the building keeps its standing as Retain does, broken down or not.
                    raw.Add(new(choice, repair.Pawn, null, repair.Duration / GameTime.TicksPerDay, 0, 0, m.BrokenDown ? horizon : 0));
                    break;
                case FixBreakdownWork fix:
                    if (fix.SuccessChance <= 0)
                    {
                        raw.Add(new(choice, fix.Pawn, new ServicingStanding.Excluded(ServicingRules.ZeroRate), 0, 0, 0, 0));
                        break;
                    }
                    if (f.Values?.GetValueOrDefault(fix.Component) is not { } component)
                    {
                        raw.Add(new(choice, fix.Pawn, new ServicingStanding.Unassessed(f.Values is null ? MaterialValues : "material value:" + fix.Component.Value), 0, 0, 0, 0));
                        break;
                    }
                    var days = fix.Duration / fix.SuccessChance / GameTime.TicksPerDay;
                    raw.Add(new(choice, fix.Pawn, null, days, component.PerUnit / fix.SuccessChance, 0, days));
                    break;
                case ReplacementWork swap:
                    if (f.Values is not { } values || swap.NetInputs.FirstOrDefault(i => !values.ContainsKey(i.Resource)) is { } unpriced)
                    {
                        var fact = f.Values is null ? MaterialValues : "material value:" + swap.NetInputs.First(i => !f.Values.ContainsKey(i.Resource)).Resource.Value;
                        raw.Add(new(choice, swap.Pawn, new ServicingStanding.Unassessed(fact), 0, 0, 0, 0));
                        break;
                    }
                    // A door swapped for a wall loses the door's function for good.
                    raw.Add(new(choice, swap.Pawn, null, swap.Duration / GameTime.TicksPerDay, swap.NetInputs.Sum(i => i.Count * values[i.Resource].PerUnit), 0, horizon));
                    break;
            }
        }
    }

    // Prices the measured alternatives with the service term and picks the cheapest, Retain first on a tie.
    private static TransitionDecision Decide(TransitionCandidate c, double horizon, List<Raw> raw)
    {
        var service = c.Service ?? new TransitionService.Unassessed("service:" + c.Definition);
        var measured = raw.Where(r => r.Standing is null).ToArray();
        var least = measured.Length == 0 ? 0 : measured.Min(r => Lost(r, horizon));
        var output = new List<ServicingAlternative>();
        foreach (var r in raw)
        {
            if (r.Standing is { } standing)
            {
                output.Add(new(r.Kind, r.Pawn, standing));
                continue;
            }
            var lost = Lost(r, horizon);
            if (service is TransitionService.Assessed { PerDay: var perDay, StartDay: var start })
            {
                var days = Math.Max(0, Math.Min(r.To, horizon) - Math.Max(r.From, start));
                output.Add(new(r.Kind, r.Pawn, new ServicingStanding.Priced(new(r.Labor, r.Material, perDay * days))));
            }
            else if (lost > least + Tolerance) output.Add(new(r.Kind, r.Pawn, new ServicingStanding.Unassessed(((TransitionService.Unassessed)service).Fact)));
            else output.Add(new(r.Kind, r.Pawn, new ServicingStanding.Priced(new(r.Labor, r.Material, 0))));
        }
        var chosen = output.Where(a => a.Standing is ServicingStanding.Priced)
            .OrderBy(a => ((ServicingStanding.Priced)a.Standing).Price.Total, Comparer<double>.Create((x, y) => Math.Abs(x - y) <= Tolerance ? 0 : x.CompareTo(y)))
            .ThenBy(a => a.Kind == ServicingChoiceKind.Retain ? 0 : 1).ThenBy(a => a.Kind).ThenBy(a => a.Pawn, StringComparer.Ordinal).FirstOrDefault();
        return new(c.Target, c.Definition, c.Cause, [.. output], chosen);
    }

    private static double Lost(Raw r, double horizon) => Math.Max(0, Math.Min(r.To, horizon) - r.From);

    /// <summary>
    /// The transition facts of a review: the coupled forecast's native servicing read, the review's material values, the default
    /// horizon, and the read's servicing options joined to <paramref name="rows"/> (none without them).
    /// </summary>
    public static TransitionFacts TransitionFactsOf(ReviewFacts f, Func<string, BuildingRow?>? rows = null)
    {
        var t = TransitionFactsOf(f.ForecastObserved.Coupled?.Servicing, f.CoupledAlternatives?.Context.MaterialValues, rows);
        return t with { Standing = StandingServiceOf(f, t), BuildPrice = BuildPriceOf(f, t.Values), Planned = f.LayoutPlan?.Transitions ?? [], Unserved = TransitionBridgingRules.UnservedOf(f) };
    }

    /// <summary>
    /// What building a planned piece new costs (TransitionDestination.Avoided): its ordinary build stuff's work and cost list
    /// (ColonyPlanningRules.RoomPieceOf) at the material values (LayoutClusterRules.PiecePrice); unread naming the piece's
    /// catalog row or a cost material's value.
    /// </summary>
    public static Func<WantedPiece, TransitionBuild> BuildPriceOf(ReviewFacts f, IReadOnlyDictionary<Resource, ConstructionMaterialValue>? values)
    {
        var stock = new Lazy<IReadOnlyDictionary<Resource, long>>(() => ResourceStockRules.Count(f.Census.Resources));
        return piece =>
        {
            if (ColonyPlanningRules.RoomPieceOf(f, stock.Value, piece.DefName, piece.Size) is not { } built) return new TransitionBuild.Unread("build price:" + piece.DefName);
            if (values is null) return new TransitionBuild.Unread(MaterialValues);
            if (built.Costs.FirstOrDefault(a => !values.ContainsKey(a.Resource)) is { } unpriced) return new TransitionBuild.Unread("material value:" + unpriced.Resource.Value);
            return new TransitionBuild.Priced(LayoutClusterRules.PiecePrice((built.Work, built.Costs), values));
        };
    }

    /// <summary>The service-read facts the occupancy and room-contribution terms name while unpriced.</summary>
    public const string RoomContribution = "room contribution:", CommonRoomMood = "common room mood:", EnvironmentBeauty = "environment beauty:", BillInProgress = "bill in progress:";

    /// <summary>
    /// A standing building's service (TransitionService) as the colony reads it:
    /// (a) occupancy: a bed its owners sleep in serves each owner the rest it frees over the ground (ProspectiveCapacityRules.RestLoss
    /// at the bed's rest effectiveness), a bench with an active bill is in use (unassessed, named <c>bill in progress:&lt;id&gt;</c>);
    /// (b) room contribution: what the piece's Beauty (Thing.GetBeauty) and wealth (Thing.MarketValue) give the room it stands in,
    /// priced by the room-quality valuation (RoomQualityRules.ImprovementValueOf) of taking it out, which moves the room's
    /// Impressiveness over its beauty, wealth and the space its footprint takes: its owners' bedroom gain over the horizon, a day.
    /// A piece in a room no one sleeps in gives nothing a bedroom thought reads; there its positive beauty (the beauty need of the
    /// pawns passing) and a common room's dining or recreation mood are unpriced and named. Unread mechanics leave it unassessed.
    /// </summary>
    public static Func<ClearanceTarget, TransitionService> StandingServiceOf(ReviewFacts f, TransitionFacts t) =>
        StandingServiceOf(f.Sleeping, (pawn, effectiveness) => ProspectiveCapacityRules.RestLoss(f, pawn, effectiveness), new Lazy<RoomImprovementFacts>(() => new()
        {
            Gains = BedroomGainFacts.Of(f),
            Levels = f.Impressiveness,
            Values = t.Values,
            Horizon = t.HorizonDays,
        }), t);

    /// <summary>StandingServiceOf over the sleeping census, an owner's rest loss a day in a bed of a rest effectiveness, and the room-quality facts.</summary>
    public static Func<ClearanceTarget, TransitionService> StandingServiceOf(SleepingObservation sleeping, Func<string, double, ExpectedLoss> rest, Lazy<RoomImprovementFacts> improvement, TransitionFacts t) =>
        row => StandingService(sleeping, rest, improvement, t, row);

    private static TransitionService StandingService(SleepingObservation sleeping, Func<string, double, ExpectedLoss> rest, Lazy<RoomImprovementFacts> improvement, TransitionFacts t, ClearanceTarget row)
    {
        if (sleeping.Beds.FirstOrDefault(b => b.Id == row.EntityId) is { Owners.Count: > 0 } bed)
        {
            var perDay = 0.0;
            foreach (var owner in bed.Owners)
            {
                if (rest(owner.Value, bed.RestEffectiveness) is not ExpectedLoss.PerDay { CapabilityDays: var days })
                    return new TransitionService.Unassessed("rest need:" + owner.Value);
                perDay += days;
            }
            return new TransitionService.Assessed(perDay, 0);
        }
        if (row.InUse) return new TransitionService.Unassessed(BillInProgress + row.EntityId);
        if (t.Read?.Transitions.FirstOrDefault(m => m.Id == row.EntityId) is not { } m) return new TransitionService.Unassessed("transition target:" + row.EntityId);
        TransitionService Unroomed() => m.Beauty > 0 ? new TransitionService.Unassessed(EnvironmentBeauty + row.DefName) : new TransitionService.Assessed(0, 0);
        if (m.Room is not TransitionRoom.Inside { Id: var roomId } || sleeping.Rooms.FirstOrDefault(r => r.Id == roomId) is not { } room) return Unroomed();
        if (room.Role is RoomRole.DiningRoom or RoomRole.RecRoom) return new TransitionService.Unassessed(CommonRoomMood + room.Role);
        PawnId[] owners = [.. sleeping.Beds.Where(b => b.Room == room.Id).SelectMany(b => b.Owners).Distinct()];
        if (owners.Length == 0) return Unroomed();
        var cells = (row.Maximum.X - row.Minimum.X + 1) * (row.Maximum.Z - row.Minimum.Z + 1);
        return RoomQualityRules.ImprovementValueOf(room, owners, new RoomPieceEffect(-m.Beauty, -m.Wealth, -cells, 0, []), improvement.Value) switch
        {
            RoomImprovementValue.Assessed { Gain: var gain } => new TransitionService.Assessed(-gain / improvement.Value.Horizon, 0),
            RoomImprovementValue.NoGain => new TransitionService.Assessed(0, 0),
            RoomImprovementValue.Unassessed => new TransitionService.Unassessed(RoomContribution + room.Id),
            var other => throw new InvalidOperationException("room contribution: " + other),
        };
    }

    /// <summary>The transition facts of one native read and the material values per unit.</summary>
    public static TransitionFacts TransitionFactsOf(ServicingActivityRead? read, IReadOnlyDictionary<Resource, ConstructionMaterialValue>? values, Func<string, BuildingRow?>? rows = null) => new()
    {
        Read = read,
        Values = values,
        Servicing = read is not null && rows is not null ? ServicingRules.Survey(read, rows) : [],
    };

    /// <summary>
    /// A host's spare as its owning model (the host comparison) values it once the plan hosts its recipes elsewhere: nothing while
    /// every replacement serves, unassessed naming <c>replacement not serving:&lt;recipe&gt;</c> otherwise.
    /// </summary>
    public static TransitionService HostSpareService(string? unserved) =>
        unserved is null ? new TransitionService.Assessed(0, 0) : new TransitionService.Unassessed(ReplacementNotServing + unserved);

    /// <summary>
    /// A spare bed's service as the prospective sleeping changes value it (task 6.3): the earliest dated sleeper's loss a day from
    /// its arrival; unassessed naming an undated arrival or an unassessed loss; nothing while no sleeper is expected.
    /// </summary>
    public static TransitionService SpareBedService(IEnumerable<ExpectedChange> changes, long tick)
    {
        TransitionService? earliest = null;
        var at = double.PositiveInfinity;
        foreach (var change in changes.Where(c => c.Owner == ExpectedOwner.Sleeper))
        {
            if (change.At is ExpectedTiming.Unread { Fact: var fact }) return new TransitionService.Unassessed(fact);
            if (change.At is not ExpectedTiming.Dated { Tick: var arrives }) continue;
            if (change.Loss is ExpectedLoss.Unassessed { Fact: var loss }) return new TransitionService.Unassessed(loss);
            var start = Math.Max(0, (arrives - tick) / (double)GameTime.TicksPerDay);
            if (change.Loss is ExpectedLoss.PerDay { CapabilityDays: var perDay } && start < at)
            {
                at = start;
                earliest = new TransitionService.Assessed(perDay, start);
            }
        }
        return earliest ?? new TransitionService.Assessed(0, 0);
    }
}
