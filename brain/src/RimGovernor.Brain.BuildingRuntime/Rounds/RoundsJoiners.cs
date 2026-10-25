namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the joiners family (#3302): its full read (the owned rounds census) and the work of its
/// ready Needs, committed under the Need and admitted by claims. <c>joiners/quests</c> works quest sites, hacks,
/// expeditions and hospitality; <c>joiners/decrees</c> its harvest fields and hunts (its production batches are
/// declared to the work ledger, <see cref="DeclareOrdersAsync"/>); <c>joiners/monuments</c>,
/// <c>joiners/departures</c> and <c>joiners/grav-inspection</c> their quest's own work. The offer, letter and
/// quest acceptances are the family's pure proposals.
/// </summary>
public sealed partial class JoinersRounds
{
    private readonly Rounder reviewer;
    private readonly Lock mu = new();
    private bool decreeWork;
    private (GenerationSnapshot World, RoundsReading Reading, DateTimeOffset Started)? last;

    public JoinersRounds(Rounder reviewer) =>
        this.reviewer = reviewer ?? throw new ControlException("JoinersRounds: reviewer is missing");

    /// <summary>The family's scheduler entry: it wakes on its reads and on the outcomes of every kind its Methods carry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<JoinersFacts>(new JoinersFamily(), ReadAsync, Schedule, WorkAsync);

    /// <summary>An optional entry woken by the outcomes of the quest, letter, site, decree, monument and hosting work it commits.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds =
        [
            ActionKind.QuestAccept, ActionKind.DialogAnswer, ActionKind.QuestShuttle, ActionKind.HackDesignation, ActionKind.CaravanDeparture,
            ActionKind.MineAcquisition, ActionKind.PrisonerInteraction, ActionKind.Assign, ActionKind.ZoneCreate, ActionKind.Acquisition,
            ActionKind.MoveBuilding, ActionKind.Building,
        ],
    };

    /// <summary>Reads the census in <paramref name="scope"/>'s world; throws when the native boundary is another world or older than the review.</summary>
    internal async Task<JoinersFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new JoinersFacts();
        }
        var reading = await ObserveAsync(scope, ct).ConfigureAwait(false);
        return JoinersFacts.From(reading.Reading.Projection.Facts);
    }

    /// <summary>The work of a ready Need; a Need whose Methods are all proposals has none.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, JoinersFacts facts, CancellationToken call)
    {
        var idle = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        var need = work.Need.Id;
        if (work.Immediate || !(need == JoinersFamily.Quests || need == JoinersFamily.Decrees || need == JoinersFamily.Monuments
            || need == JoinersFamily.Departures || need == JoinersFamily.GravInspection))
        {
            return idle;
        }
        var (read, started) = await ReadingAsync(work.State.Snapshot, call).ConfigureAwait(false);
        if (need == JoinersFamily.Quests)
        {
            // Site, hack, expedition and hosting work are independent: each runs, held only by its claims and its
            // own MethodIds. The Need reports the first admission, else the first handled step, else the first wait.
            var steps = new List<(Verdict Verdict, bool Handled)>
            {
                AdmitSiteWork(work, read, started, call),
                AdmitIdeologyQuestWork(work, read, started, call),
                AdmitExpedition(work, read, started, call),
                AdmitHospitality(work, read, started, call),
            };
            return QuestVerdict(steps, idle);
        }
        var review = await reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var (verdict, handled) = need == JoinersFamily.Decrees ? await AdmitDecreeAsync(work, review, read, started, call).ConfigureAwait(false)
            : need == JoinersFamily.Monuments ? await AdmitMonumentAsync(work, review, read, started, call).ConfigureAwait(false)
            : need == JoinersFamily.Departures ? AdmitDeparture(work, read, started, call)
            : AdmitGravInspection(read, call);
        return handled || !verdict.IsZero ? verdict : idle;
    }

    /// <summary>The first admitted step's verdict, else the first handled step's, else the first wait an unhandled step names, else <paramref name="idle"/>.</summary>
    internal static Verdict QuestVerdict(IReadOnlyList<(Verdict Verdict, bool Handled)> steps, Verdict idle)
    {
        foreach (var (verdict, handled) in steps)
        {
            if (handled && verdict == Verdict.BuildingReasonAdmitted)
            {
                return verdict;
            }
        }
        foreach (var (verdict, handled) in steps)
        {
            if (handled)
            {
                return verdict;
            }
        }
        foreach (var (verdict, _) in steps)
        {
            if (!verdict.IsZero)
            {
                return verdict;
            }
        }
        return idle;
    }

    /// <summary>The step's reading: the family read's when it is of <paramref name="world"/>, otherwise a fresh one.</summary>
    private async Task<(RoundsReading Reading, DateTimeOffset Started)> ReadingAsync(GenerationSnapshot world, CancellationToken ct)
    {
        lock (mu)
        {
            if (last is { } held && held.World == world)
            {
                return (held.Reading, held.Started);
            }
        }
        return await ObserveAsync(new FamilyReadScope(world, false), ct).ConfigureAwait(false);
    }

    private async Task<(RoundsReading Reading, DateTimeOffset Started)> ObserveAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var world = scope.World;
        var review = await reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, world, review.Tick))
        {
            throw new ControlException("joiners read: the scope is another world or older than the review");
        }
        var started = reviewer.Clock.Now();
        var reading = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, ct).ConfigureAwait(false);
        lock (mu)
        {
            last = (world, reading, started);
        }
        return (reading, started);
    }

    /// <summary>
    /// The MethodId's refusal budget over its latest attempt under the Need, unless an attempt is still open work
    /// (the commit then continues it).
    /// </summary>
    private (Verdict Verdict, bool Admitted) AdmitMethod(NeedWork work, MethodId method, CancellationToken call)
    {
        var journal = reviewer.Player.Journal;
        if (work.Owner.Methods.Any(m => m.Method == method && WorkProgress.Open(journal.LoadPlan(m.Plan, call).Progress)))
        {
            return (default, true);
        }
        return RoundsRefusalBudget.AdmitLatestAttempt(journal, work.Owner, method, work.State.Snapshot, call);
    }

    /// <summary>Throws when the world or the session moved since the read, or the read is older than the review's age bound.</summary>
    private void Fence(NeedWork work, DateTimeOffset started, string what, CancellationToken call)
    {
        var p = reviewer.Player;
        p.Current(call, work.Epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != work.State || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException(what + ": session state changed or the read took too long");
        }
    }

    /// <summary>Checks the budget, fences and commits <paramref name="plan"/> as <paramref name="method"/> under the Need: admitted, continued or held on a claim.</summary>
    private Verdict Commit(NeedWork work, MethodId method, PlanSpec plan, DateTimeOffset started, string what, CancellationToken call)
    {
        var (refused, admitted) = AdmitMethod(work, method, call);
        if (!admitted)
        {
            return refused;
        }
        Fence(work, started, what, call);
        return RoundsBuildingPlanner.OwnerCommitVerdict(reviewer.Player.Journal.CommitOwnerWork(work.Owner, method, "", plan, call));
    }

    /// <summary>Claims <paramref name="pawns"/> and <paramref name="resources"/> on the step's arbiter; true when the pass has none.</summary>
    private static bool Claim(NeedWork work, IReadOnlyList<PawnId> pawns, params string[] resources) =>
        work.Arbiter is not { } arbiter || arbiter.TryClaim(pawns, resources);
}
