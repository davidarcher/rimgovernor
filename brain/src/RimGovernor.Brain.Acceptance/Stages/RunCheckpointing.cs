namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The checkpoint ring and stage planning the runner calls (<see cref="IRunCheckpointing"/>), over
/// <see cref="RingPlanning"/> and <see cref="StagePlanning"/>. A suite's cached-stage query is
/// <see cref="StagePlanning.CachedStage"/>.
/// </summary>
public sealed class RunCheckpointing : IRunCheckpointing
{
    private readonly StoreSnapshotter? _snapshotStore;

    /// <summary>
    /// <paramref name="snapshotStore"/> copies a service's SQLite store into a bundle (the ring holds no store hook of its
    /// own); null for a harness that runs no service.
    /// </summary>
    public RunCheckpointing(StoreSnapshotter? snapshotStore = null)
    {
        _snapshotStore = snapshotStore;
    }

    /// <inheritdoc />
    public string RingExcluded(AcceptanceCase c) => RingPlanning.RingExcluded(c);

    /// <inheritdoc />
    public Fingerprint Fingerprint(AcceptanceCase c, string configDir) => RingPlanning.Fingerprint(c, configDir);

    /// <inheritdoc />
    public ResumePlan PlanResume(AcceptanceCase c, RunOptions options, TextWriter log) => RingPlanning.PlanResume(c, options, log);

    /// <inheritdoc />
    public StagePlan PlanStage(AcceptanceCase c, RunOptions options, ResumePlan resumed, TextWriter log) =>
        StagePlanning.PlanStage(c, options, resumed, log);

    /// <inheritdoc />
    public RunOptions CheckBreak(AcceptanceCase c, RunOptions options) => RingPlanning.CheckBreak(c, options);

    /// <inheritdoc />
    public void CheckThrough(AcceptanceCase c, RunOptions options) => StagePlanning.CheckThrough(c, options);

    /// <inheritdoc />
    public CheckpointRing? NewRing(AcceptanceCase c, RunOptions options, IRingHost host, Config config, string output, ResumePlan resumed, StagePlan staged, Report report) =>
        RingPlanning.NewRing(c, options, host, config, output, resumed, staged, report, _snapshotStore);

    /// <inheritdoc />
    public CheckpointRing? NewStages(AcceptanceCase c, RunOptions options, IRingHost host, Config config, string output, StagePlan plan, Report report) =>
        RingPlanning.NewStages(c, options, host, config, output, plan, report, _snapshotStore);

    /// <inheritdoc />
    public Task CloseRingAsync(CheckpointRing? ring, ResumePlan resumed, Exception? runError, Report report, Func<CancellationToken, Task>? reclaim) =>
        RingPlanning.CloseRingAsync(ring, resumed, runError, report, reclaim);

    /// <inheritdoc />
    public Task BreakRingAsync(AcceptanceCase c, RunOptions options, CheckpointRing ring, ResumePlan resumed, BreakException broke, Report report, CancellationToken cancellationToken) =>
        RingPlanning.BreakRingAsync(c, options, ring, resumed, broke, report, cancellationToken);

    /// <inheritdoc />
    public void RewindRing(string dir, ResumePlan resumed, Exception openError, Report report) =>
        RingPlanning.RewindRing(dir, resumed, openError, report);
}
