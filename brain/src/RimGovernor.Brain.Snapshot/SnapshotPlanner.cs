namespace RimGovernor.Brain.Snapshot;

/// <summary>
/// One planner step as recorded: the pure decisions it made and the inputs each took, the native site reads among
/// them. The rounds's facts say which goals open; this says how an excavation proceeds, which the planner decides
/// from its own colony read at step time.
/// </summary>
public sealed class SnapshotPlanner
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    public ConcernId Concern { get; set; }

    /// <summary>The tunnel corridor search the step ran, if any.</summary>
    public List<ExcavationSiteRequest> Excavation { get; } = [];

    /// <summary>Every native excavation site read, in order.</summary>
    public List<ExcavationRead> Sites { get; } = [];

    /// <summary>Every shrine defender read, in order.</summary>
    public List<IReadOnlyList<ShrineDefenderFacts>> ShrineSquads { get; } = [];

    /// <summary>Every ShrineBreachReadiness request.</summary>
    public List<ShrineReadinessRequest> ShrineReadiness { get; } = [];

    /// <summary>Every SelectResourceMethod request: the fresh bench census and ingredient stock a production bill is chosen from.</summary>
    public List<ResourceMethodRequest> ResourceMethods { get; } = [];

    /// <summary>Every SelectWorkshopBench request: the recipe catalog and bench census a workshop bench is chosen from.</summary>
    public List<WorkshopRequest> Workshops { get; } = [];

    /// <summary>Every SelectGearMethod request.</summary>
    public List<GearPlanningRequest> GearMethods { get; } = [];

    /// <summary>Every research selection's fresh census.</summary>
    public List<ResearchCall> Research { get; } = [];

    internal bool IsEmpty => Excavation.Count + Sites.Count + ShrineSquads.Count + ShrineReadiness.Count + ResourceMethods.Count + Workshops.Count + GearMethods.Count + Research.Count == 0;
}

/// <summary>One research selection's inputs: the staged policy (armor rungs applied), the recorded research needs and the native research read without its observation context.</summary>
public sealed record ResearchCall(RoundsPolicy Policy, IReadOnlyList<string> Needs, ResearchRead Read);

/// <summary>One native excavation site read: what was asked (Purpose names the planner's question) and the reply without its observation context.</summary>
public sealed record ExcavationRead(string Purpose, ExcavationTarget Target, IReadOnlyList<Cell> Cells, ExcavationSite Site);

/// <summary>Collects one step's <see cref="SnapshotPlanner"/>; the recording scope of the step that started it.</summary>
public sealed class PlannerRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly SnapshotPlanner _planner;
    private readonly PlannerRecorder? _previous;
    private bool _disposed;

    internal PlannerRecorder(ConcernId goal, PlannerRecorder? previous)
    {
        _planner = new SnapshotPlanner { Concern = goal };
        _previous = previous;
    }

    internal void Add(Action<SnapshotPlanner> f)
    {
        lock (_lock)
        {
            f(_planner);
        }
    }

    internal SnapshotPlanner? Finish(GenerationSnapshot current, Tick tick)
    {
        lock (_lock)
        {
            if (_planner.IsEmpty)
            {
                return null;
            }
            _planner.Snapshot = current;
            _planner.Tick = tick;
            _planner.Recorded = SnapshotPackage.Provenance(current, tick);
            return _planner;
        }
    }

    /// <summary>Ends the recording scope and restores the enclosing step's recorder.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SnapshotPackage.Restore(_previous);
    }
}

public static partial class SnapshotPackage
{
    // The recorder rides the async flow, as Go's context value did.
    private static readonly AsyncLocal<PlannerRecorder?> Current = new();

    internal static void Restore(PlannerRecorder? previous) => Current.Value = previous;

    /// <summary>
    /// Starts a recorder for one planner step when <see cref="DirEnv"/> is set; dispose the result to end the scope
    /// and pass it to <see cref="Finish"/> to write what the step recorded. With the variable unset it records
    /// nothing and the result is a no-op scope.
    /// </summary>
    public static IDisposable StartPlanner(ConcernId goal)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DirEnv)))
        {
            return NoRecording.Instance;
        }
        var recorder = new PlannerRecorder(goal, Current.Value);
        Current.Value = recorder;
        return recorder;
    }

    /// <summary>Writes what the step recorded (nothing when it recorded nothing).</summary>
    public static void Finish(IDisposable recording, GenerationSnapshot current, Tick tick)
    {
        var dir = Environment.GetEnvironmentVariable(DirEnv);
        if (string.IsNullOrEmpty(dir) || recording is not PlannerRecorder recorder)
        {
            return;
        }
        if (recorder.Finish(current, tick) is { } planner)
        {
            RecordPlanner(dir, planner);
        }
    }

    private sealed class NoRecording : IDisposable
    {
        public static readonly NoRecording Instance = new();

        public void Dispose()
        {
        }
    }

    private static void Add(Action<SnapshotPlanner> f) => Current.Value?.Add(f);

    /// <summary>Records a tunnel corridor search's request.</summary>
    public static void NoteExcavation(CancellationToken _, ExcavationSiteRequest r) => Add(p => p.Excavation.Add(r));

    /// <summary>Records a native excavation site read.</summary>
    public static void NoteSite(CancellationToken _, string purpose, ExcavationTarget target, IReadOnlyList<Cell> cells, ExcavationSite site)
    {
        site = site with { Context = null };
        Add(p => p.Sites.Add(new ExcavationRead(purpose, target, [.. cells], site)));
    }

    /// <summary>Records a shrine defender read.</summary>
    public static void NoteShrineSquad(CancellationToken _, IReadOnlyList<ShrineDefenderFacts> squad) => Add(p => p.ShrineSquads.Add(squad));

    /// <summary>Records a shrine breach readiness request.</summary>
    public static void NoteShrineReadiness(CancellationToken _, ShrineReadinessRequest r) => Add(p => p.ShrineReadiness.Add(r));

    /// <summary>Records a production bill selection's request.</summary>
    public static void NoteResourceMethod(CancellationToken _, ResourceMethodRequest r) => Add(p => p.ResourceMethods.Add(r));

    /// <summary>Records a workshop bench selection's request.</summary>
    public static void NoteWorkshop(CancellationToken _, WorkshopRequest r) => Add(p => p.Workshops.Add(r));

    /// <summary>Records a gear method selection's request.</summary>
    public static void NoteGearMethod(CancellationToken _, GearPlanningRequest r) => Add(p => p.GearMethods.Add(r));

    /// <summary>Records a research selection's inputs.</summary>
    public static void NoteResearch(CancellationToken _, ResearchCall c)
    {
        c = c with { Read = c.Read with { Context = null } };
        Add(p => p.Research.Add(c));
    }

    /// <summary>
    /// Writes p into dir as planner-&lt;goal&gt;-&lt;tick&gt;-&lt;seq&gt;, seq the first from 1 not already taken (several steps at one paused tick
    /// each keep their own recording).
    /// </summary>
    public static void RecordPlanner(string dir, SnapshotPlanner p)
    {
        int seq;
        lock (PlannerSeqLock)
        {
            var key = $"{p.Concern.Value}-{p.Tick.Value}";
            seq = PlannerSeq.GetValueOrDefault(key) + 1;
            PlannerSeq[key] = seq;
        }
        Write(dir, "planner", $"planner-{p.Concern.Value}-{p.Tick.Value}-{seq}", p);
    }

    private static readonly object PlannerSeqLock = new();
    private static readonly Dictionary<string, int> PlannerSeq = new(StringComparer.Ordinal);
}
