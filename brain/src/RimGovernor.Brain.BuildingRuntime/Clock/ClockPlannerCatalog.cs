namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What every planner method result carries: the verdict the planner's step filed.</summary>
public interface IPlannerMethod
{
    Verdict Verdict { get; }
}

/// <summary>Declares a queued planner's dependencies and wake conditions.</summary>
internal sealed record PlannerEntry
{
    public required string Name { get; init; }

    /// <summary>Receives this planner's refusal. Multi-concern planners leave it default and file under their own name (<see cref="FilingKey"/>).</summary>
    public ConcernId Concern { get; init; }

    /// <summary>Marks a protection planner: combat, tend, rescue, recovery and shelter run in the immediate review ahead of the ordinary planners.</summary>
    public bool Immediate { get; init; }

    /// <summary>Says whether the admission cycle waits on the planner: critical for the preempt and critical priority classes, optional for the development reviews.</summary>
    public PlannerClass Class { get; init; }

    /// <summary>The action kinds the planner dispatches (or, where commented on its entry, deliberately waits on). A terminal outcome of one of these kinds is the planner's own work completing, so a wake carrying it re-runs the planner (and only planners of that kind).</summary>
    public IReadOnlyList<ActionKind> Kinds { get; init; } = [];

    /// <summary>The review census sections the planner consumes: a wake whose invalidations dirty one re-runs the planner, and a step that selects a subset reads only what its planners declare.</summary>
    public IReadOnlyList<FactSection> Sections { get; init; } = [];

    /// <summary>The sectionless families the planner plans from (world).</summary>
    public IReadOnlyList<FactFamily> Families { get; init; } = [];

    /// <summary>The planner's review cadence in game ticks, zero for the hourly default.</summary>
    public Tick Every { get; init; }

    /// <summary>Reports whether the composition wired this planner.</summary>
    public required Func<ClockSchedulerConfig, bool> Configured { get; init; }

    /// <summary>
    /// The family this entry runs (#3250); null for a catalog planner. A family entry files its verdicts under its
    /// NeedIds, not its name, and runs in the ordinary wave as well as, when it has SeriousHarm Needs, the immediate one.
    /// </summary>
    public FamilyRunner? Family { get; init; }

    /// <summary>Steps the planner and stores its result on the given result; its error is wrapped with the planner's name.</summary>
    public required Func<ClockScheduler, CancellationToken, CancellationToken, ClockSchedulerResult, StepArbiter, Task<Verdict>> Run { get; init; }

    /// <summary>The planner's cadence: its own when set, else the hourly default.</summary>
    public Tick ReviewEvery() => Every.Value > 0 ? Every : ClockPlannerCatalog.ReviewEveryDefault;

    /// <summary>
    /// The key a planner's verdict files its cause under, and so the target of its concern_transitions rows: its
    /// concern, or for a planner that serves several concerns (or none) its own name, a subject key beside the
    /// concern ids. A subject key has no progress record, so it never relabels a concern's strip.
    /// </summary>
    public ConcernId FilingKey() => string.IsNullOrEmpty(Concern.Value) ? new ConcernId(Name) : Concern;

    /// <summary>
    /// The fact families the planner may read: the family of each census section it consumes plus its sectionless
    /// families. The wake step re-runs a planner on exactly these invalidations, so a read outside them would plan
    /// on facts nothing wakes it for. Definitions and identity are ambient and declared by no one.
    /// </summary>
    public HashSet<FactFamily> Declared()
    {
        var output = new HashSet<FactFamily> { FactFamily.Definitions, FactFamily.Identity };
        foreach (var section in Sections)
        {
            output.Add(section.Family());
        }
        foreach (var family in Families)
        {
            output.Add(family);
        }
        return output;
    }

    /// <summary>
    /// Opens the planner's read-audit scope when an audit is installed (<see cref="ClockPlannerCatalog.ReadAudit"/>);
    /// null otherwise. Production installs none.
    /// </summary>
    public IDisposable? AuditReads()
    {
        var audit = ClockPlannerCatalog.ReadAudit;
        if (audit is null)
        {
            return null;
        }
        var declared = Declared();
        var name = Name;
        return ReadNoteScope.With((family, source) =>
        {
            if (!declared.Contains(family))
            {
                audit(name, family, source);
            }
        });
    }

}

/// <summary>The routine planners in queue order and the rules that select them.</summary>
internal static class ClockPlannerCatalog
{
    // Review cadences, in game ticks (2500 an hour): how long a planner's last evaluation stands before the due
    // queue re-runs it without evidence. Evidence (an outcome of its kinds, a dirty section it declares) re-runs
    // it sooner.
    public static readonly Tick ReviewEveryDefault = new(GameTime.TicksPerHour);

    /// <summary>Called for every fact family a catalog planner reads that its entry does not declare. Only a test host installs one; production reads unaudited.</summary>
    public static Action<string, FactFamily, string>? ReadAudit { get; set; }

    // Census dependencies let each planner read only its required sections. Building planners need spatial facts
    // as well as the colony summary.
    private static readonly FactSection[] SectionsBuilding = [FactSection.Colony, FactSection.PlanningCells, FactSection.Zones, FactSection.Buildings, FactSection.Rooms];
    // The throne light refuel picks a hauler from the colonists.
    private static readonly FactSection[] SectionsThrone = [FactSection.Colony, FactSection.PlanningCells, FactSection.Zones, FactSection.Buildings, FactSection.Rooms, FactSection.Pawns];
    private static readonly FactSection[] SectionsColony = [FactSection.Colony];
    // The ritual plan reads the ideoligion, the building sites, the colonists and the emergency census.
    private static readonly FactSection[] SectionsRituals = [FactSection.Colony, FactSection.Pawns, FactSection.Emergency, FactSection.Buildings, FactSection.Ideology];
    private static readonly FactSection[] SectionsPawns = [FactSection.Pawns];
    private static readonly FactSection[] SectionsMedical = [FactSection.Pawns, FactSection.Colony];
    private static readonly FactSection[] SectionsThreat = [FactSection.Pawns, FactSection.Emergency];
    // Adds the population census for the owned-pawn names and the colony facts for the reading, drug and food policies.
    private static readonly FactSection[] SectionsWork = [FactSection.Pawns, FactSection.Population, FactSection.Emergency, FactSection.Colony, FactSection.Buildings];
    private static readonly FactSection[] SectionsRecovery = [FactSection.Pawns, FactSection.Emergency, FactSection.Colony];
    // The sectionless families planners declare beside their sections.
    private static readonly FactFamily[] FamiliesEmergency = [FactFamily.Emergency];

    private static PlannerEntry E<T>(
        string name,
        PlannerClass cls,
        ActionKind[] kinds,
        FactSection[] sections,
        Func<ClockSchedulerConfig, bool> configured,
        Func<ClockScheduler, CancellationToken, CancellationToken, StepArbiter, Task<T>> step,
        Action<ClockSchedulerResult, T> store,
        ConcernId concern = default,
        bool immediate = false,
        FactFamily[]? families = null,
        Tick every = default)
        where T : IPlannerMethod => new()
        {
            Name = name,
            Concern = concern,
            Immediate = immediate,
            Class = cls,
            Kinds = kinds,
            Sections = sections,
            Families = families ?? [],
            Every = every,
            Configured = configured,
            Run = async (s, ct, epoch, output, arbiter) =>
            {
                var method = await step(s, ct, epoch, arbiter).ConfigureAwait(false);
                store(output, method);
                return method.Verdict;
            },
        };

    private const PlannerClass C = PlannerClass.Critical;
    private const PlannerClass O = PlannerClass.Optional;

    /// <summary>Lists every routine planner in queue order. Planners start in this order.</summary>
    public static readonly IReadOnlyList<PlannerEntry> Entries =
    [
        E("work", O, [ActionKind.WorkAssignment, ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy, ActionKind.Building], SectionsWork,
            c => c.Work is not null, (s, ct, e, a) => s.Config.Work!.StepAsync(a, ct, e), (o, m) => o.Work = m, EnsureWorkAssignments),
        E("routes", O, [ActionKind.Building, ActionKind.DoorControl, ActionKind.OwnedDraft, ActionKind.Movement], SectionsBuilding,
            c => c.Routes is not null, (s, ct, e, a) => s.Config.Routes!.StepAsync(a, ct, e), (o, m) => o.Routes = m, MaintainRoutes),
        E(DefensePlanner, C, [ActionKind.OwnedDraft, ActionKind.Subdue, ActionKind.Ability], SectionsThreat,
            c => c.Defense is not null, (s, ct, e, a) => s.Config.Defense!.StepAsync(a, ct, e), (o, m) => o.Defense = m, immediate: true),
        E("moodRelief", O, [ActionKind.Ability], SectionsPawns,
            c => c.MoodRelief is not null, (s, ct, e, a) => s.Config.MoodRelief!.StepAsync(a, ct, e), (o, m) => o.MoodRelief = m),
        E("recovery", C, [ActionKind.RecoveryService, ActionKind.WorkAssignment, ActionKind.Husbandry], SectionsRecovery,
            c => c.Recovery is not null, (s, ct, e, a) => s.Config.Recovery!.StepAsync(a, ct, e), (o, m) => o.Recovery = m, immediate: true),
        E("dialog", C, [ActionKind.DialogAnswer], SectionsColony,
            c => c.Dialog is not null, (s, ct, e, a) => s.Config.Dialog!.StepAsync(a, ct, e), (o, m) => o.Dialog = m),
        E("defenseLayout", O, [ActionKind.Building, ActionKind.RecoveryService, ActionKind.CoverClearance, ActionKind.FoundationRemoval], SectionsBuilding,
            c => c.DefenseLayout is not null, (s, ct, e, a) => s.Config.DefenseLayout!.StepAsync(a, ct, e), (o, m) => o.DefenseLayout = m, EnsureDefensiveLayout),
    ];

    /// <summary>
    /// The due-queue entry of a family (#3250), named by its id: it wakes on an invalidation of any fact family it
    /// reads (every census section of those families, and the sectionless ones by name), on a terminal outcome of
    /// its Methods' kinds and on its cadence. It is immediate when the family has SeriousHarm Needs.
    /// </summary>
    public static PlannerEntry FamilyEntry(FamilyRunner runner)
    {
        var families = runner.Family.Reads.Union(runner.Family.SeriousHarmReads).Distinct().ToArray();
        return new PlannerEntry
        {
            Name = runner.Family.Id,
            Family = runner,
            Immediate = runner.Immediate,
            Class = runner.Schedule.Class,
            Kinds = runner.Schedule.Kinds,
            Sections = [.. families.SelectMany(FactStore.FamilySections).Distinct()],
            Families = families,
            Every = runner.Schedule.Every,
            Configured = c => c.Rounds is not null,
            Run = async (s, ct, epoch, output, arbiter) =>
            {
                var p = s.Config.Rounds!.Player;
                var state = p.Session.State();
                if (!state.Enabled)
                {
                    output.Families[runner.Family.Id] = new FamilyRun { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
                    return BuildingRuntimePackage.BuildingReasonDisabled;
                }
                if (!state.ObservationKnown || !state.Snapshot.IsValid)
                {
                    throw new ControlException("step: observation unknown or snapshot invalid");
                }
                var run = await runner.RunAsync(new FamilyStep
                {
                    Journal = p.Journal,
                    World = state.Snapshot,
                    Immediate = ReviewScope.Immediate,
                    State = state,
                    Epoch = epoch,
                    Arbiter = arbiter,
                    Fence = () =>
                    {
                        p.Current(ct, epoch);
                        if (p.Session.State() != state)
                        {
                            throw new ControlException("step: session state changed");
                        }
                    },
                }, ct).ConfigureAwait(false);
                output.Families[runner.Family.Id] = run;
                return run.Verdict;
            },
        };
    }

    /// <summary>The catalog with each family's entry after the planners; a family named like a planner or another family is refused.</summary>
    public static List<PlannerEntry> WithFamilies(IReadOnlyList<FamilyRunner> families)
    {
        var output = new List<PlannerEntry>(Entries);
        var names = output.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var runner in families)
        {
            if (!names.Add(runner.Family.Id))
            {
                throw new ControlException($"family {runner.Family.Id} is named like another planner or family");
            }
            output.Add(FamilyEntry(runner));
        }
        return output;
    }
}

public sealed partial class ClockScheduler
{
    /// <summary>
    /// Queues the configured planners <paramref name="pick"/> selects (all when null) onto the wave, returning the
    /// names queued in catalog order.
    /// </summary>
    internal List<string> QueuePlanners(PlannerWave wave, StepArbiter arbiter, Func<PlannerEntry, bool>? pick, CancellationToken ct, CancellationToken epoch)
    {
        var queued = new List<string>();
        foreach (var catalogEntry in _catalog)
        {
            var entry = catalogEntry;
            if (!entry.Configured(Config) || pick is not null && !pick(entry))
            {
                continue;
            }
            if (ReviewScope.Immediate && ImmediatePlanner(entry))
            {
                entry = entry with { Class = PlannerClass.Critical };
            }
            queued.Add(entry.Name);
            wave.Queue(this, arbiter, entry, epoch);
        }
        return queued;
    }
}
