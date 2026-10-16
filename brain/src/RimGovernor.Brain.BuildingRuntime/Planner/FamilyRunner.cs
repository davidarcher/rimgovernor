namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What one family read is for: the world it reads and whether it is the immediate pass's narrow read.</summary>
public readonly record struct FamilyReadScope(GenerationSnapshot World, bool Immediate);

/// <summary>
/// How the scheduler runs a family besides what the family itself declares: its class, the action kinds whose
/// terminal outcomes wake it (its Methods' outcomes) and its cadence. Its fact families come from
/// <see cref="IFamily.Reads"/> and <see cref="IFamily.SeriousHarmReads"/>.
/// </summary>
public sealed record FamilySchedule
{
    public PlannerClass Class { get; init; } = PlannerClass.Optional;

    public IReadOnlyList<ActionKind> Kinds { get; init; } = [];

    /// <summary>
    /// The kinds of an immediate family's open SeriousHarm work the immediate pass watches as protection; null for
    /// every kind in <see cref="Kinds"/>. A family whose SeriousHarm Needs also carry ordinary-pass work (the medical
    /// family's amputation bills) names only its protection kinds.
    /// </summary>
    public IReadOnlyList<ActionKind>? ProtectionKinds { get; init; }

    /// <summary>The review cadence in game ticks, zero for the hourly default.</summary>
    public Tick Every { get; init; }
}

/// <summary>One Method the run committed or continued for a Need.</summary>
public readonly record struct FamilyMethod(NeedId Need, MethodId Method, PlanId Plan, ClaimOutcome Outcome);

/// <summary>
/// What one family run came to (#3250): where each derived Need stood, the verdict filed under each NeedId, the
/// Methods committed or continued, the open plans of its SeriousHarm Needs (the immediate pass's protection watch)
/// and every fact family the read touched outside what the pass declares.
/// </summary>
public sealed record FamilyRun : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public IReadOnlyList<NeedStanding> Standings { get; init; } = [];

    public IReadOnlyList<NeedReport> Reports { get; init; } = [];

    public IReadOnlyList<FamilyMethod> Methods { get; init; } = [];

    public IReadOnlyList<PlanId> SeriousHarmPlans { get; init; } = [];

    public IReadOnlyList<FactFamily> UndeclaredReads { get; init; } = [];

    /// <summary>The run derived every Need of the family (a reviewed full pass): a Need it did not report no longer exists. False for the immediate pass, which judges only SeriousHarm Needs, and for a run that judged nothing.</summary>
    public bool Complete { get; init; }

    /// <summary>Verdicts the run files under keys other than its NeedIds: the work ledger's purpose reports (#3304). Empty for every other family.</summary>
    public IReadOnlyList<LedgerReport> Filed { get; init; } = [];
}

/// <summary>A verdict filed under one NeedId and the Method the Need works (admitted, continued or still open; empty when none).</summary>
public readonly record struct NeedReport(NeedId Need, Verdict Verdict, MethodId Method = default);

/// <summary>What one family step runs against: the journal, the world, the pass and the fence a commit runs behind.</summary>
internal sealed record FamilyStep
{
    public required JournalStore Journal { get; init; }

    public required GenerationSnapshot World { get; init; }

    public bool Immediate { get; init; }

    /// <summary>Throws when the world or the session moved since the step began; runs before every commit.</summary>
    public Action Fence { get; init; } = static () => { };

    /// <summary>The session state the step began on; a building step fences its own commits against it.</summary>
    public ControlState State { get; init; }

    /// <summary>The player epoch the step runs under.</summary>
    public CancellationToken Epoch { get; init; }

    /// <summary>The step's pawn and resource arbiter, when the pass has one.</summary>
    public StepArbiter? Arbiter { get; init; }
}

/// <summary>
/// One ready Need's asynchronous work (#3279): the Need, its owner as loaded for the step's world, and the step's
/// session state, epoch and arbiter. Building work (native previews, room reconciles) runs here between choosing
/// and committing, and commits under <see cref="Owner"/> through the shared building planner, so every commit is a
/// claimed Need commit: once per target, refused with a claim conflict on a held footprint.
/// </summary>
public sealed record NeedWork
{
    public required Need Need { get; init; }

    public required NeedOwner Owner { get; init; }

    public required ControlState State { get; init; }

    public CancellationToken Epoch { get; init; }

    public StepArbiter? Arbiter { get; init; }

    public bool Immediate { get; init; }
}

/// <summary>
/// The runtime half of a family (#3250): one scheduler entry per family. It reads the family's facts (in the
/// immediate pass the narrow read), derives its Needs, evaluates preconditions, files a verdict under each NeedId,
/// and commits the Methods the family proposes for its ready Needs through the Need owner's claimed commit.
/// </summary>
public abstract class FamilyRunner
{
    private readonly Lock _mu = new();
    private IReadOnlyList<PlanId> _seriousHarmPlans = [];

    private protected FamilyRunner(IFamily family, FamilySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(schedule);
        if (!new NeedId(family.Id + "/need").IsValid)
        {
            throw new ControlException($"family id \"{family.Id}\" is not a NeedId family segment");
        }
        Family = family;
        Schedule = schedule;
    }

    public IFamily Family { get; }

    public FamilySchedule Schedule { get; }

    /// <summary>Whether the family has SeriousHarm Needs: it then also runs in the immediate pass.</summary>
    public bool Immediate => Family.SeriousHarmReads.Count > 0;

    /// <summary>The fact families the run may read in the pass: the narrow ones in the immediate pass, all otherwise; Definitions and Identity are ambient.</summary>
    public IReadOnlySet<FactFamily> Declared(bool immediate)
    {
        var output = new HashSet<FactFamily> { FactFamily.Definitions, FactFamily.Identity };
        output.UnionWith(Family.SeriousHarmReads);
        if (!immediate)
        {
            output.UnionWith(Family.Reads);
        }
        return output;
    }

    /// <summary>The open plans of the SeriousHarm Needs the latest immediate run saw; the immediate pass watches their protection actions.</summary>
    internal IReadOnlyList<PlanId> SeriousHarmPlans
    {
        get
        {
            lock (_mu)
            {
                return _seriousHarmPlans;
            }
        }
    }

    internal async Task<FamilyRun> RunAsync(FamilyStep step, CancellationToken call)
    {
        var run = await StepAsync(step, call).ConfigureAwait(false);
        if (step.Immediate)
        {
            lock (_mu)
            {
                _seriousHarmPlans = run.SeriousHarmPlans;
            }
        }
        return run;
    }

    private protected abstract Task<FamilyRun> StepAsync(FamilyStep step, CancellationToken call);

    /// <summary>
    /// The verdict a family entry reports for itself (the due queue reads it): admitted when any Method was, open
    /// work when one continued, otherwise the highest-ranked NeedId report, or nothing to do.
    /// </summary>
    internal static Verdict EntryVerdict(IReadOnlyList<NeedReport> reports)
    {
        if (reports.Any(r => r.Verdict.Outcome == Outcome.Admitted))
        {
            return BuildingRuntimePackage.BuildingReasonAdmitted;
        }
        if (reports.Any(r => r.Verdict == BuildingRuntimePackage.BuildingReasonExistingWork))
        {
            return BuildingRuntimePackage.BuildingReasonExistingWork;
        }
        var best = BuildingRuntimePackage.BuildingReasonNoDeficit;
        var rank = 1;
        foreach (var report in reports)
        {
            if (PlannerReasons.RecordReason(report.Verdict) is { } note && PlannerReasons.NoteRank(note) > rank)
            {
                best = report.Verdict;
                rank = PlannerReasons.NoteRank(note);
            }
        }
        return best;
    }
}

/// <summary>A family over its own fact type, with the full read and the immediate pass's narrow read.</summary>
/// <param name="read">Reads the facts for the scope; in the immediate pass only the SeriousHarm families, the rest left unknown.</param>
/// <param name="work">
/// The family's asynchronous step for each ready Need after its pure proposals (#3279), or null. It runs behind the
/// step's fence and returns the Need's verdict; Methods it commits under the Need owner are reported on the run.
/// </param>
/// <param name="filed">The verdicts a full pass files beside the family's Needs (<see cref="FamilyRun.Filed"/>), or null for none.</param>
public sealed class FamilyRunner<TFacts>(IFamily<TFacts> family, Func<FamilyReadScope, CancellationToken, Task<TFacts>> read, FamilySchedule? schedule = null,
    Func<NeedWork, TFacts, CancellationToken, Task<Verdict>>? work = null, Func<GenerationSnapshot, CancellationToken, IReadOnlyList<LedgerReport>>? filed = null)
    : FamilyRunner(family, schedule ?? new FamilySchedule())
{
    private readonly IFamily<TFacts> _family = family;
    private readonly Func<FamilyReadScope, CancellationToken, Task<TFacts>> _read = read ?? throw new ArgumentNullException(nameof(read));
    private readonly Func<NeedWork, TFacts, CancellationToken, Task<Verdict>>? _work = work;
    private readonly Func<GenerationSnapshot, CancellationToken, IReadOnlyList<LedgerReport>>? _filed = filed;

    private protected override async Task<FamilyRun> StepAsync(FamilyStep step, CancellationToken call)
    {
        var review = await step.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != step.World)
        {
            return new FamilyRun { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var facts = await ReadAudited(step, call).ConfigureAwait(false);
        var standings = PolicyFunctions.NeedStandings(_family.Id, _family.Derive(facts.Facts));
        if (step.Immediate)
        {
            standings = [.. standings.Where(s => s.Need.Tier == NeedTier.SeriousHarm)];
        }

        var reports = new Dictionary<NeedId, Verdict>();
        var ready = new List<Need>();
        foreach (var standing in standings)
        {
            switch (standing.Readiness)
            {
                case NeedReadiness.Met:
                    reports[standing.Need.Id] = BuildingRuntimePackage.BuildingReasonNoDeficit;
                    break;
                case NeedReadiness.Unknown:
                    // Unknown never counts as met: the Need is reported unmeasured and nothing is proposed.
                    reports[standing.Need.Id] = Verdict.FieldUnavailable(standing.Need.Id.Value);
                    break;
                case NeedReadiness.Waiting:
                    reports[standing.Need.Id] = Verdict.WaitFor(Cause.WaitingOnNeed, standing.WaitingOn.Value);
                    break;
                default:
                    ready.Add(standing.Need);
                    reports[standing.Need.Id] = Verdict.AwaitingPlan(standing.Need.Id.Value, "no_method");
                    break;
            }
        }

        var methods = new List<FamilyMethod>();
        var working = new Dictionary<NeedId, MethodId>();
        if (ready.Count > 0)
        {
            var readyIds = ready.Select(n => n.Id).ToHashSet();
            var proposed = new HashSet<NeedId>();
            foreach (var candidate in _family.Propose(ready, facts.Facts))
            {
                if (!readyIds.Contains(candidate.Need))
                {
                    throw new ControlException($"family {_family.Id} proposed for {candidate.Need}, which is not ready");
                }
                var (verdict, method) = await Task.Run(() => Commit(step, candidate, call), call).ConfigureAwait(false);
                if (method is { } committed)
                {
                    methods.Add(committed);
                    working.TryAdd(committed.Need, committed.Method);
                }
                // The first candidate's verdict replaces the no-method default; later ones only strengthen it.
                reports[candidate.Need] = proposed.Add(candidate.Need) ? verdict : Stronger(reports[candidate.Need], verdict);
            }
            if (_work is not null)
            {
                foreach (var need in ready)
                {
                    var (verdict, worked) = await Work(step, need, facts.Facts, call).ConfigureAwait(false);
                    methods.AddRange(worked);
                    if (worked.Count > 0)
                    {
                        working[need.Id] = worked[^1].Method;
                    }
                    // A Need nothing was proposed for takes its work's verdict (a wait names what it waits on);
                    // otherwise the work only strengthens what the proposals filed.
                    reports[need.Id] = proposed.Contains(need.Id) ? Stronger(reports[need.Id], verdict) : verdict;
                }
            }

            foreach (var need in ready.Where(n => !proposed.Contains(n.Id) && !working.ContainsKey(n.Id)))
            {
                // Nothing new proposed or worked while earlier Methods are still at work: the Need works them, and
                // waits on them unless its step said something stronger than no method.
                if (await Task.Run(() => OpenMethod(step, need.Id, call), call).ConfigureAwait(false) is { IsEmpty: false } open)
                {
                    working[need.Id] = open;
                    if (reports[need.Id] == Verdict.AwaitingPlan(need.Id.Value, "no_method"))
                    {
                        reports[need.Id] = BuildingRuntimePackage.BuildingReasonExistingWork;
                    }
                }
            }
        }

        var seriousHarm = new List<PlanId>();
        if (step.Immediate)
        {
            foreach (var standing in standings)
            {
                var owner = await Task.Run(() => step.Journal.LoadNeed(standing.Need.Id, step.World, call), call).ConfigureAwait(false);
                seriousHarm.AddRange(owner.Methods.Select(m => m.Plan));
            }
        }

        var filed = standings.Select(s => new NeedReport(s.Need.Id, reports[s.Need.Id], working.GetValueOrDefault(s.Need.Id))).ToList();
        return new FamilyRun
        {
            Verdict = EntryVerdict(filed),
            Standings = standings,
            Reports = filed,
            Methods = methods,
            SeriousHarmPlans = seriousHarm,
            UndeclaredReads = facts.Undeclared,
            Complete = !step.Immediate,
            Filed = _filed is null || step.Immediate ? [] : await Task.Run(() => _filed(step.World, call), call).ConfigureAwait(false),
        };
    }

    /// <summary>Reads the facts under a read note: a family read outside the pass's declaration is recorded on the run and reported to the catalog audit.</summary>
    private async Task<(TFacts Facts, IReadOnlyList<FactFamily> Undeclared)> ReadAudited(FamilyStep step, CancellationToken call)
    {
        var declared = Declared(step.Immediate);
        var undeclared = new List<FactFamily>();
        var audit = ClockPlannerCatalog.ReadAudit;
        TFacts facts;
        using (ReadNoteScope.With((family, source) =>
        {
            if (declared.Contains(family))
            {
                return;
            }
            lock (undeclared)
            {
                if (!undeclared.Contains(family))
                {
                    undeclared.Add(family);
                }
            }
            audit?.Invoke(_family.Id, family, source);
        }))
        {
            facts = await _read(new FamilyReadScope(step.World, step.Immediate), call).ConfigureAwait(false);
        }
        lock (undeclared)
        {
            return (facts, [.. undeclared]);
        }
    }

    /// <summary>The latest Method bound to the Need that still has open work; empty when none does.</summary>
    private static MethodId OpenMethod(FamilyStep step, NeedId need, CancellationToken call) =>
        step.Journal.LoadNeed(need, step.World, call).Methods.LastOrDefault(m => Standard.WorkOpen(step.Journal.LoadPlan(m.Plan, call).Progress))?.Method ?? default;

    /// <summary>
    /// Files a hold, or checks the Method's refusal budget, mints the plan, fences and commits one candidate through
    /// the Need owner. The budget runs over the MethodId's latest attempt, keyed by it, unless an attempt is still open
    /// work (the commit then continues it).
    /// </summary>
    private static (Verdict Verdict, FamilyMethod? Method) Commit(FamilyStep step, NeedMethod candidate, CancellationToken call)
    {
        if (candidate.Hold is { } hold)
        {
            return (Verdict.AwaitingPlan(hold.Subject, hold.Detail), null);
        }
        var owner = step.Journal.LoadNeed(candidate.Need, step.World, call);
        if (!owner.Methods.Any(m => m.Method == candidate.Method && Standard.WorkOpen(step.Journal.LoadPlan(m.Plan, call).Progress)))
        {
            var (refused, admitted) = BuildingRuntimePackage.AdmitLatestAttempt(step.Journal, owner, candidate.Method, step.World, call);
            if (!admitted)
            {
                return (refused, null);
            }
        }
        var id = PlanIds.MintPlanId();
        var plan = candidate.Plan(id);
        if (plan.Id != id)
        {
            throw new ControlException($"need {candidate.Need} method {candidate.Method.Value} built a plan under another id");
        }
        step.Fence();
        NeedCommit commit;
        try
        {
            commit = step.Journal.CommitClaimedMethod(candidate.Need, step.World, candidate.Method, candidate.Reason, plan, call);
        }
        catch (NotAdmittedException e)
        {
            // A Safeguard vetoes routine work: the Need waits on nothing it can do.
            return (Verdict.Refuse(Cause.SharedAdmission, e.Message, ""), null);
        }
        catch (ConstructionLaborException)
        {
            // The initial shelter holds the builders: this Need waits, its siblings still run.
            return (Verdict.AwaitingPlan("construction_labor", "initial_shelter"), null);
        }
        catch (ProductionMaterialException)
        {
            return (Verdict.AwaitingPlan("production_material", "insufficient_unknown_or_bounded"), null);
        }
        switch (commit.Outcome)
        {
            case ClaimOutcome.Admitted:
                return (BuildingRuntimePackage.BuildingReasonAdmitted, new FamilyMethod(candidate.Need, candidate.Method, id, ClaimOutcome.Admitted));
            case ClaimOutcome.Continued:
                var open = commit.Owner.Methods.LastOrDefault(m => m.Method == candidate.Method);
                return (BuildingRuntimePackage.BuildingReasonExistingWork,
                    open is null ? null : new FamilyMethod(candidate.Need, candidate.Method, open.Plan, ClaimOutcome.Continued));
            default:
                return (Verdict.ClaimHeld(commit.Conflict!.Claim), null);
        }
    }

    /// <summary>Runs the family's work for one ready Need under its owner and reports the Methods it added.</summary>
    private async Task<(Verdict Verdict, IReadOnlyList<FamilyMethod> Methods)> Work(FamilyStep step, Need need, TFacts facts, CancellationToken call)
    {
        var before = await Task.Run(() => step.Journal.LoadNeed(need.Id, step.World, call), call).ConfigureAwait(false);
        step.Fence();
        Verdict verdict;
        try
        {
            verdict = await _work!(new NeedWork { Need = need, Owner = before, State = step.State, Epoch = step.Epoch, Arbiter = step.Arbiter, Immediate = step.Immediate }, facts, call).ConfigureAwait(false);
        }
        catch (NotAdmittedException e)
        {
            // A Safeguard vetoes routine work: the Need waits on nothing it can do.
            return (Verdict.Refuse(Cause.SharedAdmission, e.Message, ""), []);
        }
        catch (ConstructionLaborException)
        {
            // The starter shelter holds the builders: this Need waits, its siblings still run.
            return (Verdict.AwaitingPlan("construction_labor", "initial_shelter"), []);
        }
        var after = await Task.Run(() => step.Journal.LoadNeed(need.Id, step.World, call), call).ConfigureAwait(false);
        var seen = before.Methods.Select(m => m.Plan).ToHashSet();
        return (verdict, [.. after.Methods.Where(m => !seen.Contains(m.Plan)).Select(m => new FamilyMethod(need.Id, m.Method, m.Plan, ClaimOutcome.Admitted))]);
    }

    /// <summary>Of two verdicts for one Need, the one it files: admitted, then open work, then a claim or Safeguard refusal over no method.</summary>
    private static Verdict Stronger(Verdict held, Verdict next)
    {
        static int Rank(Verdict v) => v.Outcome == Outcome.Admitted ? 3 : v == BuildingRuntimePackage.BuildingReasonExistingWork ? 2 : v.Is(Cause.AwaitingPlan) ? 0 : 1;
        return Rank(next) > Rank(held) ? next : held;
    }
}
