using System.Globalization;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the layout family (#3256): its full read (the owned census for home coverage and the stone
/// shell, the rooms census for the climate envelope, this tick's firebreak ring review, and the stored defensive
/// layout with the map's fights, #3314) and the work steps of the Needs that preview and admit buildings (the
/// envelope's room waves, the stone shell's wall bundles and the defensive layout's tiers), each committing under
/// the Need owner and admitted by claims. Home coverage and the firebreak are pure proposals.
/// </summary>
public sealed partial class LayoutRounds
{
    private readonly Rounder _reviewer;
    private readonly LayoutNeeds _needs;
    private readonly FirebreakMemory? _firebreak;
    private readonly FacadeNativeClient? _stone;
    private readonly RoundsBuildingPlanner? _building;
    private readonly RoundsBuildingPlanner? _routes;
    private readonly RoundsDefenseLayoutPlanner? _defense;

    /// <summary>
    /// The family with <paramref name="needs"/> composed. The firebreak reads its ring through
    /// <paramref name="firebreak"/>, which the review drives every tick (its dwell clock is the ring plan's); the
    /// stone shell previews and lists wall sites through <paramref name="stone"/>; the defensive layout reads its site,
    /// access and pawns and previews its tiers through <paramref name="defense"/>.
    /// </summary>
    public LayoutRounds(Rounder reviewer, LayoutNeeds needs, FacadeNativeClient? firebreak, FacadeNativeClient? stone, FacadeNativeClient? defense = null)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("LayoutRounds: reviewer or its native source is missing");
        }
        _reviewer = reviewer;
        _needs = needs ?? throw new ControlException("LayoutRounds: needs are missing");
        if (needs.Firebreak)
        {
            _firebreak = new FirebreakMemory(firebreak ?? throw new ControlException("LayoutRounds: the firebreak needs a defense site source"));
            reviewer.Firebreak = _firebreak;
        }
        if (needs.StoneShell)
        {
            _stone = stone ?? throw new ControlException("LayoutRounds: the stone shell needs a wall site source");
        }
        if (needs.Defense)
        {
            _defense = new RoundsDefenseLayoutPlanner(reviewer, defense ?? throw new ControlException("LayoutRounds: the defensive layout needs a defense site source"));
        }
        if (needs.ClimateEnvelope && reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner
            {
                Reviewer = reviewer,
                Native = source,
                Concern = LayoutFamily.ClimateEnvelope.FilingKey,
                Need = LayoutFamily.ClimateEnvelope,
            };
        }
        if (needs.Routes)
        {
            _routes = RoundsBuildingPlanner.NewRoundsRoutesPlanner(reviewer, reviewer.Native as IRoundsBuildingSource);
        }
    }

    /// <summary>
    /// An optional entry woken by the outcomes of its area edits, ring cuts and clearances, buildings, roofs, wall and
    /// foundation removals, the killbox dig and the routes' door settings.
    /// </summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.Area, ActionKind.AreaPlantCut, ActionKind.CoverClearance, ActionKind.Building, ActionKind.BuildRoof, ActionKind.WallRemoval,
            ActionKind.FoundationRemoval, ActionKind.Excavation, ActionKind.DoorControl,
            ActionKind.OwnedDraft, ActionKind.Movement],
        // The defensive layout admits its tiers against the zone census.
        Sections = [FactSection.Zones],
    };

    /// <summary>The family's scheduler entry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<LayoutFacts>(new LayoutFamily(_needs), ReadAsync, Schedule, WorkAsync);

    /// <summary>The settled ring cells the latest firebreak review found to pave (the rooms/flooring Need paves them); empty without the firebreak.</summary>
    public List<Cell> Pave() => _firebreak?.Pave() ?? [];

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the home-area diff and the flammable owned walls
    /// from the owned census, the rooms owed a vent or a roof and the routes standing from the rooms census, the ring
    /// work this tick's review left, and the defensive layout's journal facts. A composed section that cannot be read
    /// (the review did not plan the ring this tick, no layout plan or colony ground) raises
    /// <see cref="RequiredFactException"/>.
    /// Throws when the read is from another world or older than the review.
    /// </summary>
    internal async Task<LayoutFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new LayoutFacts();
        }
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("layout read: the scope is another world or older than the review");
        }
        var facts = new LayoutFacts();
        if (_needs.HomeCoverage || _needs.StoneShell)
        {
            var claims = journal.ConstructionClaims(scope.World, expected.Tick, ct);
            var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, ct).ConfigureAwait(false);
            var f = read.Projection.Facts;
            if (_needs.HomeCoverage)
            {
                facts = facts with { HomeCoverage = HomeAreaRules.PlanHomeArea(f.MapBounds, f.CurrentConstruction, claims, f.HomeCoverage) };
            }
            if (_needs.StoneShell)
            {
                facts = facts with { StoneShell = FacilityUpkeepRules.ReviewStoneShell(ConstructionOwnershipRules.OwnedConstructions(claims, f.CurrentConstruction), f.Upkeep.Structures) };
            }
        }
        if (_needs.ClimateEnvelope || _needs.Routes)
        {
            var rooms = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), [ClimateRules.ClimateDefs().VentDef], ct).ConfigureAwait(false);
            if (_needs.ClimateEnvelope)
            {
                var (owed, known) = EnvelopeWork(rooms.Projection);
                facts = facts with { ClimateEnvelope = known ? owed : throw new RequiredFactException("climate envelope") };
            }
            if (_needs.Routes)
            {
                var projection = rooms.Projection;
                facts = facts with { Routes = LayoutFamily.RoutesStandingOf(projection.Facts.Upkeep.Routes ?? throw new RequiredFactException("routes census"), projection.Rooms, _reviewer.Policy) };
            }
        }
        if (_firebreak is not null)
        {
            var (work, ruins, ok) = _firebreak.Take(RoundsStockpilePlanner.StockpileWorld(scope.World), review.Tick);
            facts = ok
                ? facts with { Firebreak = work, FirebreakStep = FirebreakCandidate(work, ruins ?? []) }
                : throw new RequiredFactException("firebreak ring");
        }
        if (_defense is not null)
        {
            facts = facts with { Defense = RoundsDefenseLayoutPlanner.DefenseLayoutFactsOf(_reviewer, review, ct) };
        }
        return facts;
    }

    /// <summary>
    /// The asynchronous step of a ready Need: the envelope's room waves, the stone shell's wall bundles, the
    /// defensive layout's tiers or the routes' door; the proposed Needs have none.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, LayoutFacts facts, CancellationToken ct)
    {
        if (work.Need.Id == LayoutFamily.ClimateEnvelope && _needs.ClimateEnvelope)
        {
            return await EnvelopeStepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == LayoutFamily.StoneShell && _stone is not null)
        {
            return await StoneShellStepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == LayoutFamily.Defense && _defense is not null)
        {
            return await _defense.StepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == LayoutFamily.Routes && _routes is not null)
        {
            return ShelterRounds.Building(await _routes.Copy().StepAsync(work.Arbiter!, ct, work.Epoch).ConfigureAwait(false));
        }
        return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
    }

    /// <summary>
    /// The ring order the work supports: the plant cut over the cut cells and the ruin clearances (at most
    /// MaxDefenseCoverBatch), named <c>firebreak-&lt;digest&gt;</c> over the cells it orders, so the same order
    /// continues its open Method and a later order over other cells is admitted beside it. Null when nothing is owed
    /// or no action builds.
    /// </summary>
    internal static LayoutCandidate? FirebreakCandidate(FirebreakWork work, IReadOnlyDictionary<Cell, CoverClearance> ruins)
    {
        if (!work.Owed)
        {
            return null;
        }
        var probe = FirebreakPlan.FirebreakActions(PlanIds.MintPlanId(), work, ruins);
        if (probe.Actions.Count == 0)
        {
            return null;
        }
        var key = new StringBuilder();
        foreach (var cell in work.Cut)
        {
            key.Append(CultureInfo.InvariantCulture, $"cut@{cell.X},{cell.Z};");
        }
        foreach (var action in probe.Actions)
        {
            if (action.CoverClearance is { } ruin)
            {
                key.Append(CultureInfo.InvariantCulture, $"ruin@{ruin.Cell.X},{ruin.Cell.Z};");
            }
        }
        return new LayoutCandidate(
            new MethodId(FirebreakPlan.FirebreakPrefix + RoundsBuildingPlanner.DigestHex(key.ToString(), 8)),
            string.Create(CultureInfo.InvariantCulture, $"firebreak cut {probe.Cut} cells, deconstruct {probe.Ruins} ruins"),
            id => FirebreakPlan.FirebreakActions(id, work, ruins).Actions);
    }
}
