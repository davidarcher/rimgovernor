using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The economy/resource Need's composed work steps (#3303): its mines, tunnels and deep drills, its chops, forage and
/// hunts, its hay, social and supply-plan fields, and its production bench. Each is optional; the composition passes
/// the ones its toggles enable.
/// </summary>
public sealed record EconomyResourceSteps
{
    public RoundsResourcePlanner? Resource { get; init; }

    public RoundsAcquisitionPlanner? Acquisition { get; init; }

    public RoundsFieldPlanner? Fields { get; init; }

    public RoundsBuildingPlanner? Workshop { get; init; }
}

/// <summary>
/// The runtime half of the economy family (#3263, #3303). For the research Need: its read (the research the colony's
/// evidence waits on, then the native research census) and its building work, the laboratory benches raised under
/// the <c>economy/research</c> Need through the shared building planner. For the resource Need: the review's derived
/// stock targets, the stock census and the runway forecasts, and its composed work steps. For the trade Needs
/// (#3316): the review's trade measures and the trade missions record; the trade Need's export batches are the work
/// ledger's, the caravan Need's work is the caravan session phases and the trade-mission Need's the settlement trip.
/// Every Method is the Need's, admitted by its claims.
/// </summary>
public sealed class EconomyRounds
{
    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient _native;
    private readonly RoundsBuildingPlanner? _building;
    private readonly bool _research;
    private readonly EconomyResourceSteps? _resource;
    private readonly bool _trade;
    private readonly RoundsTradePlanner? _caravan;

    /// <summary>The trade Need's work: none of the family's own. Its one remedy is the export batches the work ledger places.</summary>
    internal static readonly Verdict ExportBillVerdict = Verdict.WaitFor(Cause.Facility, "export_bills");

    /// <param name="reviewer">The rounder the family reviews through.</param>
    /// <param name="native">The research census source.</param>
    /// <param name="research">Compose the research Need.</param>
    /// <param name="resource">The resource Need's steps; null leaves the Need out.</param>
    /// <param name="trade">Compose the trade Need, whose export batches the work ledger declares.</param>
    /// <param name="caravan">The caravan and trade-mission Needs' planner; null leaves them out.</param>
    public EconomyRounds(Rounder reviewer, FacadeNativeClient native, bool research = true, EconomyResourceSteps? resource = null, bool trade = false, RoundsTradePlanner? caravan = null)
    {
        _reviewer = reviewer ?? throw new ControlException("EconomyRounds: reviewer is missing");
        _native = native ?? throw new ControlException("EconomyRounds: native is missing");
        _research = research;
        _resource = resource;
        _trade = trade;
        _caravan = caravan;
        if (research && native is IRoundsBuildingSource source && native is IRoundsSource)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = EconomyFamily.Research.FilingKey, Need = EconomyFamily.Research };
        }
    }

    /// <summary>The outcomes the family's work commits: research selection, buildings, mines, deconstructions, zones, acquisitions, trade phases and caravan departures.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Kinds =
        [
            ActionKind.ResearchSelect, ActionKind.Building, ActionKind.MineAcquisition, ActionKind.Deconstruction, ActionKind.Excavation,
            ActionKind.ZoneCreate, ActionKind.ZoneCellEdit, ActionKind.Acquisition, ActionKind.AcquisitionWithdraw,
            ActionKind.Trade, ActionKind.CommsTradeRequest, ActionKind.CaravanDeparture,
        ],
        // The resource review and the trade read the stockpile zones.
        Sections = [FactSection.Zones],
    };

    /// <summary>The family's scheduler entry: it wakes on its reads and on its Methods' outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<EconomyFacts>(new EconomyFamily(_research, _resource is not null, _trade, _caravan is not null), ReadAsync, Schedule, WorkAsync);

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the research needs (the production ladder's
    /// rung, planners' research waits, the census requests, deep drilling) and the native
    /// research census. Throws when the read is from another world, older than the review, or slower than the
    /// review's age bound.
    /// </summary>
    internal async Task<EconomyFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new EconomyFacts();
        }
        var started = _reviewer.Clock.Now();
        var p = _reviewer.Player;
        var review = await p.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("economy read: the scope is another world or older than the review");
        }
        var items = await _reviewer.ItemFactsAsync(scope.World, ct).ConfigureAwait(false);
        var needs = RoundsProjectBill.RoundsResearchNeeds(p.Journal, _reviewer.Policy, items, scope.World, _reviewer.EquipmentOwed(scope.World), ct);
        needs = _reviewer.CensusResearchNeeds(needs);
        needs = DeepDrillRules.DeepDrillingResearch(needs, review.ResourceRunwayState(), items);
        var facts = new EconomyFacts { ResearchNeeds = [.. needs] };
        {
            var read = await _native.ReadResearchAsync(Boundary.Identity(scope.World), ct).ConfigureAwait(false);
            try
            {
                Boundary.Context(read.Context, scope.World);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("economy read: research context", e);
            }
            if (read.Context.Tick < review.Tick.Value)
            {
                throw new ControlException("economy read: research read older than the review");
            }
            var catalog = await _native.DefinitionCatalogAsync(Boundary.Identity(scope.World), ct).ConfigureAwait(false);
            facts = facts with { Research = EconomyRules.ResearchCensusOf(read, catalog.Research, catalog.KnowledgeOverflow) };
        }
        if (_resource is not null)
        {
            facts = facts with { Resource = await ReadResourceAsync(scope, review, expected, facts, ct).ConfigureAwait(false) };
        }
        if (_trade || _caravan is not null)
        {
            facts = facts with { Trade = _reviewer.TradeMemory.Get(scope.World) };
        }
        if (_caravan is not null)
        {
            var missions = await p.Journal.LoadTradeMissionsAsync(Player.PlayerWorld(scope.World), ct).ConfigureAwait(false);
            facts = facts with { Missions = new TradeMissionFacts { Active = missions.Missions.Count != 0, Proposed = _caravan.MissionProposed() } };
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("economy read: the read took too long");
        }
        return facts;
    }

    /// <summary>
    /// The resource Need's facts: the review's derived targets and works, the colony's stock census and wood, the
    /// review's runway forecasts, and the research the recorded needs name first.
    /// </summary>
    private async Task<ResourceFacts> ReadResourceAsync(FamilyReadScope scope, RoundsReview review, Identity expected, EconomyFacts economy, CancellationToken ct)
    {
        var demand = _reviewer.Demand.Get(scope.World);
        var claims = _reviewer.Player.Journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, ct).ConfigureAwait(false);
        var research = EconomyRules.ResearchSummary(economy.Research);
        var observed = reading.Projection.Facts with
        {
            ResourceRunways = review.ResourceRunwayState(),
            ResearchNeeds = economy.ResearchNeeds,
            Research = research,
        };
        return ResourceFacts.From(observed, demand);
    }

    /// <summary>
    /// The research Need's building work beside its pure selection: the simple bench while a project is current
    /// or the next one waits on it, and the high-tech bench and its analyzer ahead of need. Each is a Method of
    /// the Need, admitted by its claims, so they go side by side with the selection. The bench's verdict is the
    /// Need's when the next project waits on that bench; otherwise only an admission or open work is reported,
    /// and the pure hold stands.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, EconomyFacts facts, CancellationToken ct)
    {
        var idle = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        if (work.Need.Id == EconomyFamily.Resource && _resource is { } steps)
        {
            // A held resource Need (the research gate) has no work of its own: its pure hold stands.
            return EconomyRules.ResourceHold(facts.Resource) is not null ? idle : await ResourceWorkAsync(work, steps, _caravan, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == EconomyFamily.Trade)
        {
            return ExportBillVerdict;
        }
        if (work.Need.Id == EconomyFamily.Caravan && _caravan is not null)
        {
            return (await _caravan.CaravanAsync(work, ct).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id == EconomyFamily.TradeMission && _caravan is not null)
        {
            return (await _caravan.MissionAsync(work, ct).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id != EconomyFamily.Research || _building is null)
        {
            return idle;
        }
        var step = EconomyRules.ResearchStepOf(facts);
        var result = idle;
        if (step.Bench)
        {
            var bench = await BenchAsync(work, false, ct).ConfigureAwait(false);
            if (bench.Outcome == Outcome.Admitted || bench == BuildingReasonExistingWork
                || (step.Hold?.Subject == "research_bench" && bench != BuildingReasonNoDeficit))
            {
                result = bench;
            }
        }
        if (step.BuildAhead)
        {
            var ahead = await BenchAsync(work, true, ct).ConfigureAwait(false);
            if (ahead.Outcome == Outcome.Admitted)
            {
                result = ahead;
            }
        }
        return result;
    }

    /// <summary>
    /// The resource Need's steps, independent work under one Need: each runs, held only by its claims and its own
    /// MethodIds. The Need reports the first admission, else the first step's reason; a forecast trade request the
    /// supply plan proposed is held first (<see cref="RoundsTradePlanner.RequestHold"/>).
    /// </summary>
    private static async Task<Verdict> ResourceWorkAsync(NeedWork work, EconomyResourceSteps steps, RoundsTradePlanner? trade, CancellationToken ct)
    {
        var verdicts = new List<Verdict>();
        if (trade?.RequestHold(work, ct) is { } hold)
        {
            verdicts.Add(hold);
        }
        if (steps.Resource is { } resource)
        {
            verdicts.Add((await resource.StepAsync(work.Arbiter!, work.Owner, ct, work.Epoch).ConfigureAwait(false)).Verdict);
        }
        if (steps.Acquisition is { } acquisition)
        {
            verdicts.Add((await acquisition.StepAsync(work.Arbiter!, ct, work.Epoch, work.Owner).ConfigureAwait(false)).Verdict);
        }
        if (steps.Fields is { } fields)
        {
            verdicts.Add((await fields.StepAsync(work.Arbiter!, work.Owner, ct, work.Epoch).ConfigureAwait(false)).Verdict);
        }
        if (steps.Workshop is { } workshop)
        {
            verdicts.Add((await workshop.Copy().StepAsync(work.Arbiter!, ct, work.Epoch).ConfigureAwait(false)).Verdict);
        }
        return FoodRounds.SupplyVerdict(work.Need.Id, verdicts);
    }

    private async Task<Verdict> BenchAsync(NeedWork work, bool advanced, CancellationToken ct)
    {
        var copy = _building!.Copy();
        copy.AdvancedLab = advanced;
        return (await copy.StepAsync(work.Arbiter!, ct, work.Epoch).ConfigureAwait(false)).Verdict;
    }
}

public sealed partial class RoundsBuildingPlanner
{
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectResearchBench(ColonyProjection facts)
    {
        var bench = facts.Shapes.Furniture.BenchFor(RoomRole.Laboratory);
        if (AdvancedLab)
        {
            var (owed, ok) = AdvancedLabOwed(facts);
            if (!ok)
            {
                return (null, BuildingReasonNoDeficit);
            }

            bench = owed;
        }

        PlanningDefinition? definition = null;
        foreach (var d in facts.Definitions)
        {
            if (d.Name == bench)
            {
                definition = d;
            }
        }

        if (definition is null)
        {
            return (null, FieldUnavailable("research_bench_definition"));
        }

        var available = definition.Available;

        if (!available)
        {
            return (null, BuildingReasonResearchBenchUnavailable);
        }

        var facility = RoomsRules.Facility(RoomRole.Laboratory);
        var resolved = Copy();
        resolved.Definition = bench;
        resolved.Environment = PlacementEnvironment.Indoors;
        resolved.Facility = facility;
        resolved.Stuff = facts.BulkBuildStuff(bench, 1);
        return (resolved, default);
    }

    internal static (string Definition, bool Owed) AdvancedLabOwed(ColonyProjection facts)
    {
        var furniture = facts.Shapes.Furniture;
        if (furniture.AdvancedLab == "")
        {
            return ("", false);
        }

        var standing = new HashSet<string>();
        foreach (var c in facts.Cells)
        {
            var d = c.PlayerEdifice();
            if (d != "")
            {
                standing.Add(d);
            }
        }

        var next = furniture.AdvancedLab;
        if (standing.Contains(next))
        {
            next = furniture.Analyzer.Def;
        }

        if (standing.Contains(next))
        {
            return ("", false);
        }

        foreach (var d in facts.Definitions)
        {
            if (d.Name == next)
            {
                var available = d.Available;
                if (available)
                {
                    return (next, true);
                }

                return ("", false);
            }
        }

        return ("", false);
    }
}
