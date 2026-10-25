using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the ideology family (#3260): one owned read gives the owed role assignments, the rituals
/// that can begin now and the reform the ideoligion owes. The pending reform lives in the
/// <c>family/ideology/reform</c> record: the read settles it (observed or stale records are dropped) and reuses its
/// intent while it still matches; the work step records the intent once the reform's Method is open.
/// </summary>
public sealed class IdeologyReader
{
    private readonly Rounder _reviewer;
    private readonly bool _roles;
    private readonly bool _rituals;
    private readonly bool _reform;
    private readonly bool _convert;

    public IdeologyReader(Rounder reviewer, bool roles, bool rituals, bool reform, bool convert)
    {
        _reviewer = reviewer ?? throw new ControlException("IdeologyReader: reviewer is missing");
        _roles = roles;
        _rituals = rituals;
        _reform = reform;
        _convert = convert;
    }

    /// <summary>The family's scheduler entry; each toggle derives its Need.</summary>
    public static FamilyRunner<IdeologyFacts> Runner(Rounder reviewer, bool roles, bool rituals, bool reform, bool convert)
    {
        var reader = new IdeologyReader(reviewer, roles, rituals, reform, convert);
        return new FamilyRunner<IdeologyFacts>(new IdeologyFamily(roles, rituals, reform, convert), reader.ReadAsync, Schedule, reader.WorkAsync);
    }

    /// <summary>An optional entry woken by role assignments, rituals, reforms and conversions.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.Assign, ActionKind.Ritual, ActionKind.IdeoligionReform, ActionKind.Conversion],
    };

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. Throws when the read is from another world,
    /// older than the review, or slower than the review's age bound. The immediate pass reads nothing.
    /// </summary>
    internal async Task<IdeologyFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new IdeologyFacts();
        }
        var started = _reviewer.Clock.Now();
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("ideology read: the scope is another world or older than the review");
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, call).ConfigureAwait(false);
        var projection = read.Reading;
        var facts = new IdeologyFacts { Installed = IdeologyFamilyRules.Installed(projection.Facts) };
        if (!facts.Installed)
        {
            return facts;
        }
        var ideology = projection.Facts.Ideology is IdeologyMode.Installed { Primary: var held } ? held : throw new RequiredFactException("ideoligion");
        var pawns = projection.Facts.ForecastObserved.WorkPawns.Pawns;
        if (_roles)
        {
            facts = facts with { Roles = IdeologyRolesRules.RoleAssignments(ideology, pawns, SelectedWork(read, scope.World, call)) };
        }
        if (_rituals)
        {
            facts = facts with { Rituals = RitualRules.PlanRituals(ideology, pawns, projection.Facts.RitualSites, projection.Identity.Tick, Rounder.RoundsCalm(read, scope.World)) };
        }
        if (_reform)
        {
            facts = await ReformAsync(facts, read, scope.World, call).ConfigureAwait(false);
        }
        if (_convert)
        {
            var goals = await _reviewer.Player.Journal.LoadGoalsAsync(Player.PlayerWorld(scope.World), call).ConfigureAwait(false);
            facts = facts with { Conversion = ConversionRules.Owed(goals.Goals, projection.Facts.Ideology, projection.Facts.ForecastObserved.WorkPawns) };
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("ideology read: the read took too long");
        }
        return facts;
    }

    /// <summary>
    /// The work the journal commits each pawn to (#3644): its live coupled orders (every non-retired activity Method of the
    /// current authority) and the open rows of the admin/work-assignments Need's priority writes, so a role assigned now
    /// does not disable work a review selected. The rounds review admits its coupled selection before the planner wave
    /// reads this; a priority write the same wave commits after this read is not seen.
    /// </summary>
    private WorkCommitments SelectedWork(RoundsReading read, GenerationSnapshot world, CancellationToken call)
    {
        var journal = _reviewer.Player.Journal;
        var pending = journal.LoadNeed(AdminFamily.WorkAssignments, world, call).Methods
            .Where(m => RoundsWorkPlanner.IsWorkMethod(m.Method))
            .SelectMany(m => journal.LoadPlan(m.Plan, call).Progress)
            .Where(p => WorkProgress.Open([p]))
            .Select(p => p.Action.WorkAssignment)
            .OfType<WorkAssignment>();
        WorkType? Giver(string name) => read.Catalog?.DefRow<RimWorld.WorkGiverDef>(name) is not { } giver ? null
            : giver.workType is not { } type ? WorkType.None
            : Wire.TryParse<WorkType>(type.defName, out var work) ? work : WorkType.Unrecognized;
        return IdeologyRolesRules.SelectedWork(journal.WorkActions(world, call), [.. pending], Giver);
    }

    /// <summary>
    /// The chosen design, the pending record settled against the ideoligion and the intent a Method commits. An
    /// observed reform clears the record and is met; a record whose ideoligion, reform count or expected design no
    /// longer matches is cleared; with a record held and the ideoligion or catalog unread the reform is unknown.
    /// </summary>
    private async Task<IdeologyFacts> ReformAsync(IdeologyFacts facts, RoundsReading read, GenerationSnapshot world, CancellationToken call)
    {
        var journal = _reviewer.Player.Journal;
        var goals = _reviewer.Goals ? (await journal.LoadGoalsAsync(Player.PlayerWorld(world), call).ConfigureAwait(false)).Goals : [];
        var choice = Rounder.ReformChoice(read, world, GoalRules.IdeoTargets(goals));
        var pending = (await journal.LoadIdeologyReformAsync(Player.PlayerWorld(world), call).ConfigureAwait(false))?.Reform;
        var ideo = read.Reading.Facts.Ideology is IdeologyMode.Installed { Primary: var held } ? held : throw new RequiredFactException("ideoligion");
        var current = Rounder.CurrentDesign(ideo, read.Catalog ?? throw new RequiredFactException("definition catalog"));
        var state = ideo.Facts.Development;
        facts = facts with { Development = state };
        if (pending is not null)
        {
            if (Rounder.ObservedReform(ideo, current, pending))
            {
                await journal.ClearIdeologyReformAsync(call).ConfigureAwait(false);
                return facts with { Reform = choice, ReformObserved = true };
            }
            if (state is not PbObservations.IdeoDevelopment.Fluid fluid || fluid.ReformCount != pending.Count || IdeoligionDesign.CanonicalIdeoligionDesign(current) != pending.Expected || ideo.Facts.IdeoId != pending.IdeoId)
            {
                await journal.ClearIdeologyReformAsync(call).ConfigureAwait(false);
                pending = null;
            }
        }
        facts = facts with { Reform = choice };
        if (choice.Design.Memes.Count == 0 || choice.Comparison?.Decision != DesignDecision.Improves)
        {
            return facts;
        }
        var reformCount = (state as PbObservations.IdeoDevelopment.Fluid ?? throw new RequiredFactException("fluid ideoligion development")).ReformCount;
        var intent = new IdeoligionReform(ideo.Facts.IdeoId, current, choice.Design, reformCount);
        if (pending is not null && pending.IdeoId == ideo.Facts.IdeoId && pending.Count == reformCount
            && IdeoligionDesign.CanonicalIdeoligionDesign(current) == pending.Expected && pending.Design == choice.Design)
        {
            intent = pending;
        }
        return facts with { Intent = intent };
    }

    /// <summary>
    /// The reform Need's work: once its Method for the intent holds open work, the intent is recorded so a reload or
    /// a later read settles it. Other Needs work through their pure proposals.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, IdeologyFacts facts, CancellationToken call)
    {
        var none = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        if (work.Need.Id != IdeologyFamily.Reform || facts.Intent is not { } intent)
        {
            return none;
        }
        var method = IdeologyFamily.ReformMethodId(intent);
        var journal = _reviewer.Player.Journal;
        if (!work.Owner.Methods.Any(m => m.Method == method && WorkProgress.Open(journal.LoadPlan(m.Plan, call).Progress)))
        {
            return none;
        }
        var world = Player.PlayerWorld(work.State.Snapshot);
        if ((await journal.LoadIdeologyReformAsync(world, call).ConfigureAwait(false))?.Reform is { } held && held.Count == intent.Count && held.IdeoId == intent.IdeoId
            && held.Expected == intent.Expected && held.Design == intent.Design)
        {
            return none;
        }
        int? goal = facts.Reform?.Serves is ReformServes.Goal serves ? serves.Id : null;
        await journal.SaveIdeologyReformAsync(new IdeologyReformRecord { World = world, Reform = intent, Goal = goal }, call).ConfigureAwait(false);
        return none;
    }
}
