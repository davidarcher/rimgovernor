using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The checkpoint ring: every case's run phase is bundled (save, store, sidecar) every CheckpointEvery at a natural pause
// into <root>/checkpoints/<area>/<case>/<label>/, the last Checkpoints.CheckpointKeep entries kept, plus a final "failed"
// bundle when the run fails. The next `acceptance run` of a case whose last run in this root failed resumes from the
// ring's next entry unless -fresh; the ring is keyed on the root because the root is the worktree's private game copy.

/// <summary>Ring planning, capture setup and teardown (Go <c>cases/checkpoint.go</c>).</summary>
public static class RingPlanning
{
    /// <summary>
    /// Says why c never checkpoints, or "" when it may: a case that owns its process, opts out (NoCheckpoint), or
    /// measures the paused fraction the capture would perturb (speedmatrix, tickbudget).
    /// </summary>
    public static string RingExcluded(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Start is OwnedStart)
        {
            return "the case owns its process lifecycle";
        }

        if (c.NoCheckpoint)
        {
            return "the case opts out (NoCheckpoint)";
        }

        foreach (string area in new[] { "speedmatrix/", "tickbudget/" })
        {
            if (c.Name.StartsWith(area, StringComparison.Ordinal))
            {
                return "the area measures clock timing a capture would perturb";
            }
        }

        return "";
    }

    /// <summary>
    /// What the current tree would stamp on a bundle of c: the installed native package, the case's Start and the
    /// environment's expansions. The package hash is "" (and the fingerprint unusable) until the root has been prepared
    /// once; a configDir without a config.json throws <see cref="FileNotFoundException"/>.
    /// </summary>
    public static Fingerprint Fingerprint(AcceptanceCase c, string configDir)
    {
        ArgumentNullException.ThrowIfNull(c);
        string game = InstalledGameDir(configDir);
        IReadOnlyList<string> expansions = c.Expansions.Count > 0
            ? [.. c.Expansions]
            : Acceptance.Expansions.FromEnvironment() ?? [];
        IReadOnlyDictionary<string, string>? files = null;
        try
        {
            files = Harness.PackageFiles(game);
        }
        catch (AcceptanceException)
        {
            // An unreadable package leaves the hash "" and the fingerprint unusable for a resume.
        }

        Start start = c.Start ?? throw new AcceptanceException($"case {c.Name} has no Start");
        return new Fingerprint(Checkpoints.PackageHash(files), Checkpoints.StartHash(start.Describe()), expansions);
    }

    /// <summary>
    /// The fingerprint under the run's prepared configuration, falling back to the root's base configuration: a root
    /// nothing has prepared yet (a suite worker a ring or stage bundles were carried into) still names the installed
    /// game there.
    /// </summary>
    public static Fingerprint FingerprintOrRoot(AcceptanceCase c, RunOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        try
        {
            return Fingerprint(c, opts.ConfigDir(c));
        }
        catch (FileNotFoundException)
        {
            return Fingerprint(c, Path.Combine(opts.Root, "config"));
        }
    }

    /// <summary>The game folder the configuration launches (Go <c>InstalledGameDir</c>).</summary>
    public static string InstalledGameDir(string configDir)
    {
        string path = Path.Combine(HeadlessProfile.MustAbs(configDir), "config.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} does not exist", path);
        }

        string workingDir = HeadlessProfile.GameSection(HeadlessProfile.LoadConfig(path))["workingDir"].AsString();
        return workingDir.Length == 0 ? throw new AcceptanceException($"{Path.Combine(configDir, "config.json")}: the game has no workingDir") : workingDir;
    }

    /// <summary>
    /// Reads c's ring and decides whether this run resumes, printing its decision on <paramref name="log"/>: the ring's
    /// pending note, <c>resuming &lt;case&gt; from t+7m (rev abc123, failed at t+8m); -fresh starts over</c>, or why a ring
    /// was discarded. -fresh removes the ring; -rewind steps back.
    /// </summary>
    public static ResumePlan PlanResume(AcceptanceCase c, RunOptions opts, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(log);
        string dir = opts.RingDir(c);
        if (opts.Fresh)
        {
            Checkpoints.DeleteTree(dir);
            return new ResumePlan();
        }

        if (opts.CheckpointEvery <= TimeSpan.Zero || RingExcluded(c).Length > 0)
        {
            return new ResumePlan();
        }

        Ring? previous = Checkpoints.ReadRing(dir);
        if (previous is null)
        {
            return new ResumePlan();
        }

        if (previous.Note.Length > 0)
        {
            log.WriteLine(previous.Note);
        }

        string label = previous.Next;
        if (opts.Rewind > 0 && label.Length > 0)
        {
            string rewound = previous.Rewind(opts.Rewind);
            if (rewound.Length == 0)
            {
                log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"-rewind {opts.Rewind} steps past the ring of {c.Name}; starting fresh"));
                return new ResumePlan { Previous = previous };
            }

            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"-rewind {opts.Rewind}: {rewound} instead of {label}"));
            label = rewound;
        }

        if (label.Length == 0)
        {
            return new ResumePlan { Previous = previous };
        }

        Checkpoint? entry = previous.Entry(label);
        if (entry is null)
        {
            log.WriteLine($"discarding checkpoint ring of {c.Name}: entry {label} is missing; starting fresh");
            Checkpoints.DeleteTree(dir);
            return new ResumePlan();
        }

        string sidecar = Path.Combine(entry.Path, Checkpoints.CheckpointSidecar);
        if (!File.Exists(sidecar))
        {
            log.WriteLine($"discarding checkpoint ring of {c.Name}: {sidecar} does not exist; starting fresh");
            Checkpoints.DeleteTree(dir);
            return new ResumePlan();
        }

        Fingerprint current;
        try
        {
            current = FingerprintOrRoot(c, opts);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"discarding checkpoint ring of {c.Name}: cannot fingerprint this root ({e.Message}); starting fresh");
            Checkpoints.DeleteTree(dir);
            return new ResumePlan();
        }

        string reason = current.Mismatch(entry);
        if (reason.Length > 0)
        {
            log.WriteLine($"discarding checkpoint ring of {c.Name}: {reason}; starting fresh");
            Checkpoints.DeleteTree(dir);
            return new ResumePlan();
        }

        string rev = previous.SourceRevision;
        if (rev.Length == 0)
        {
            rev = "unknown";
        }
        else if (rev.Length > 8)
        {
            rev = rev[..8];
        }

        if (previous.Break is not null && string.Equals(label, Breakpoint.BreakCheckpoint, StringComparison.Ordinal))
        {
            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"resuming {c.Name} from its breakpoint {previous.Break.Spec} (rev {rev}, tick {entry.Tick}); -fresh starts over"));
        }
        else
        {
            string failedAt = CheckpointRing.OffsetLabel(TimeSpan.FromMilliseconds(previous.FailedOffsetMs));
            log.WriteLine($"resuming {c.Name} from {label} (rev {rev}, failed at {failedAt}); -fresh starts over");
        }

        return new ResumePlan { Entry = entry, Previous = previous };
    }

    /// <summary>
    /// A ring over the run's game for c: the sides that may hold the game (the host is consulted at every capture), the
    /// store and the fingerprint. <see cref="NewRing"/> and <see cref="NewStages"/> set its cadence.
    /// </summary>
    public static CheckpointRing BaseRing(AcceptanceCase c, IRingHost host, Config cfg, string output, string dir, StoreSnapshotter? snapshotStore = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(cfg);
        var ring = new CheckpointRing(dir, c.Name, cfg)
        {
            Keep = Checkpoints.CheckpointKeep,
            StorePath = Path.Combine(output, "service.sqlite"),
            SnapshotStore = snapshotStore,
            Output = output,
            Fingerprint = Fingerprint(c, cfg.Configuration),
            SourceRevision = MetricSeries.SourceRevision(),
            Bridge = () => host.LiveHarness,

            // A restarted service is not on the case's own list; the host tracks the latest launch.
            Service = host.LatestService,
        };
        if (c.Serve is { } serve)
        {
            var families = new JsonArray();
            foreach (string family in serve.Families ?? [])
            {
                families.Add(family);
            }

            var env = new JsonArray();
            foreach (string entry in serve.Env)
            {
                env.Add(entry);
            }

            ring.Serve = new JsonObject { ["families"] = serve.Families is null ? null : families, ["env"] = env, ["resume"] = serve.Resume };
        }

        ring.Prepared = host.Prepared?.DeepCloneObject();
        return ring;
    }

    /// <summary>
    /// The ring the run captures into, null when c never checkpoints (<c>report["checkpointing"]</c> says why). A run
    /// that opened on a stage bundle (<paramref name="staged"/>) starts at the staging run's offset so labels stay monotonic.
    /// </summary>
    public static CheckpointRing? NewRing(
        AcceptanceCase c,
        RunOptions opts,
        IRingHost host,
        Config cfg,
        string output,
        ResumePlan resumed,
        StagePlan staged,
        Report report,
        StoreSnapshotter? snapshotStore = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(report);
        if (opts.CheckpointEvery <= TimeSpan.Zero)
        {
            report["checkpointing"] = "off";
            return null;
        }

        string reason = RingExcluded(c);
        if (reason.Length > 0)
        {
            report["checkpointing"] = reason;
            return null;
        }

        string dir = opts.RingDir(c);
        CheckpointRing ring;
        try
        {
            ring = BaseRing(c, host, cfg, output, dir, snapshotStore);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            report["checkpointing"] = "cannot fingerprint: " + e.Message;
            return null;
        }

        ring.Every = opts.CheckpointEvery;
        if (resumed is { Resuming: true, Previous: { } previous, Entry: { } entry })
        {
            ring.Base = TimeSpan.FromMilliseconds(entry.OffsetMs);
            ring.Prepared = entry.Prepared?.DeepCloneObject();
            ring.State = CopyState(entry.State);
            ring.Prior = [.. previous.Entries.Where(e => e.OffsetMs <= entry.OffsetMs)];
        }
        else if (staged is { Staged: true, Entry: { } stagedEntry })
        {
            ring.Base = TimeSpan.FromMilliseconds(stagedEntry.OffsetMs);
            ring.Prepared = stagedEntry.Prepared?.DeepCloneObject();
            ring.State = CopyState(stagedEntry.State);
        }

        report["checkpointing"] = string.Create(CultureInfo.InvariantCulture, $"every {GoDuration.Format(opts.CheckpointEvery)} into {ring.Dir}");
        return ring;
    }

    /// <summary>
    /// The ring stage bundles are captured into: never activated (no periodic captures, no failed bundle), keyed on the
    /// run's stage key; null when the run stages nothing or staging is off (<c>report["staging"]</c> says why).
    /// </summary>
    public static CheckpointRing? NewStages(AcceptanceCase c, RunOptions opts, IRingHost host, Config cfg, string output, StagePlan plan, Report report, StoreSnapshotter? snapshotStore = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        if (c.Stages.Count == 0)
        {
            return null;
        }

        if (plan.Off.Length > 0)
        {
            report["staging"] = "off: " + plan.Off;
            return null;
        }

        CheckpointRing ring;
        try
        {
            ring = BaseRing(c, host, cfg, output, opts.StagesDir(c), snapshotStore);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            report["staging"] = "off: cannot fingerprint: " + e.Message;
            return null;
        }

        ring.StageKey = plan.Key;
        if (plan is { Staged: true, Entry: { } entry })
        {
            ring.Prepared = entry.Prepared?.DeepCloneObject();
        }

        report["staging"] = "into " + ring.Dir;
        report["stage_key"] = plan.Key;
        return ring;
    }

    /// <summary>A sidecar's case state as a fresh object, empty when none.</summary>
    public static JsonObject CopyState(JsonObject? state) => state is { Count: > 0 } ? state.DeepCloneObject() : [];

    /// <summary>
    /// Records the run's bundles on the report and leaves the ring for the next run: a passing run clears it; a failing
    /// run keeps the entries up to the resume point plus this run's, the failed bundle (taken here, before teardown,
    /// while the game is still open) and the index naming what the next run resumes from. <paramref name="reclaim"/>,
    /// when set, takes the game back for the failed bundle after the case's own deferred service stop left nobody
    /// holding it. The failed bundle is taken under its own three-minute budget, not the run's cancelled token.
    /// </summary>
    public static async Task CloseRingAsync(
        CheckpointRing? ring,
        ResumePlan resumed,
        Exception? runError,
        Report report,
        Func<CancellationToken, Task>? reclaim)
    {
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(report);
        if (ring is null)
        {
            return;
        }

        ring.Deactivate();
        Checkpoint? failed = null;
        if (runError is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if (!ring.Held() && reclaim is not null)
            {
                try
                {
                    await reclaim(cts.Token).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    report["checkpoint_errors"] = Strings([.. ring.Errors(), "reclaim the game for the failed bundle: " + e.Message]);
                }
            }

            failed = await ring.FailAsync(cts.Token).ConfigureAwait(false);
        }

        IReadOnlyList<Checkpoint> entries = ring.Entries();
        report["checkpoints"] = Rows(entries);
        if (ring.Capped() is { Length: > 0 } capped)
        {
            report["checkpoint_capped"] = capped;
        }

        if (ring.Errors() is { Count: > 0 } errs)
        {
            report["checkpoint_errors"] = Strings(errs);
        }

        if (runError is null)
        {
            try
            {
                Checkpoints.DeleteTree(ring.Dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report["checkpoint_errors"] = Strings([.. ring.Errors(), "clear ring: " + e.Message]);
            }

            return;
        }

        var next = new Ring
        {
            Case = ring.Case,
            SourceRevision = ring.SourceRevision,
            FailedOffsetMs = (long)ring.Offset().TotalMilliseconds,
            Failed = failed,
            FailedOutput = ring.Output,
        };
        if (failed is not null)
        {
            next.FailedTick = failed.Tick;
        }

        foreach (Checkpoint e in MergeEntries(entries, resumed))
        {
            next.Entries.Add(e);
        }

        next.Plan(resumed.Previous, resumed.Entry?.Label ?? "", next.FailedTick);
        try
        {
            next.Write(ring.Dir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            report["checkpoint_errors"] = Strings([.. ring.Errors(), "write ring: " + e.Message]);
            return;
        }

        report["checkpoint_next"] = next.Next;
        if (next.Note.Length > 0)
        {
            report["checkpoint_note"] = next.Note;
        }
    }

    /// <summary>
    /// The ring the run that just ended leaves behind, minus its final bundle: this run's periodic entries plus the
    /// previous ring's up to the resume point, which are still this timeline's past; those after it belong to the
    /// attempt that ended, and the oldest past <see cref="Checkpoints.CheckpointKeep"/> are removed.
    /// </summary>
    public static IReadOnlyList<Checkpoint> MergeEntries(IReadOnlyList<Checkpoint> entries, ResumePlan resumed)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(resumed);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<Checkpoint>();
        foreach (Checkpoint e in entries)
        {
            if (e.Label is Checkpoints.FailedCheckpoint or Breakpoint.BreakCheckpoint)
            {
                continue;
            }

            merged.Add(e);
            kept.Add(e.Label);
        }

        if (resumed.Previous is not null)
        {
            foreach (Checkpoint e in resumed.Previous.Entries)
            {
                if (kept.Contains(e.Label))
                {
                    continue;
                }

                if (e.Label == Breakpoint.BreakCheckpoint)
                {
                    // The breakpoint this run continued from is consumed.
                    Checkpoints.TryDeleteTree(e.Path);
                }
                else if (resumed.Resuming && e.OffsetMs > (resumed.Entry?.OffsetMs ?? 0))
                {
                    Checkpoints.TryDeleteTree(e.Path);
                }
                else if (!resumed.Resuming)
                {
                    Checkpoints.TryDeleteTree(e.Path);
                }
                else
                {
                    merged.Add(e);
                    kept.Add(e.Label);
                }
            }
        }

        // OrderBy is stable, as the Go sort.SliceStable.
        List<Checkpoint> ordered = [.. merged.OrderBy(e => e.OffsetMs)];
        while (ordered.Count > Checkpoints.CheckpointKeep)
        {
            Checkpoints.TryDeleteTree(ordered[0].Path);
            ordered.RemoveAt(0);
        }

        return ordered;
    }

    /// <summary>
    /// Validates -break against c before the game opens: the case must checkpoint, the ring must be on, and a stage
    /// breakpoint must name a declared stage. Raises <c>CheckpointEvery</c> to the default when it was off, since the
    /// breakpoint's bundle is a ring entry.
    /// </summary>
    public static RunOptions CheckBreak(AcceptanceCase c, RunOptions opts)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        Breakpoint b = opts.Break;
        if (b.IsZero)
        {
            return opts;
        }

        if (opts.PostmortemOnly || opts.Dev || opts.Repeat > 1)
        {
            throw new AcceptanceException("-break takes a plain run: none of -postmortem-only, -repeat, or dev");
        }

        string reason = RingExcluded(c);
        if (reason.Length > 0)
        {
            throw new AcceptanceException($"-break {b}: case {c.Name} never checkpoints ({reason}), so nothing could resume from the breakpoint");
        }

        if (b.Stage.Length > 0 && !c.Stages.Contains(b.Stage, StringComparer.Ordinal))
        {
            throw new AcceptanceException(c.Stages.Count == 0
                ? $"-break {b}: case {c.Name} declares no Stages; break at tick=<n> or minute=<m> instead"
                : $"-break {b}: case {c.Name} declares the stages [{string.Join(' ', c.Stages)}]");
        }

        return opts.CheckpointEvery <= TimeSpan.Zero ? opts with { CheckpointEvery = Checkpoints.DefaultCheckpointEvery } : opts;
    }

    /// <summary>
    /// Takes the break bundle on <paramref name="ring"/> (pausing the game) and writes the ring the paused run leaves:
    /// this run's periodic entries and the timeline it resumed into, the break bundle last, Next naming it and Break
    /// recording why. The report gets "break", "checkpoints" and the ring note.
    /// </summary>
    public static async Task BreakRingAsync(
        AcceptanceCase c,
        RunOptions opts,
        CheckpointRing ring,
        ResumePlan resumed,
        BreakException broke,
        Report report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(broke);
        ArgumentNullException.ThrowIfNull(report);
        var block = new JsonObject
        {
            ["spec"] = opts.Break.ToString(),
            ["reason"] = broke.Reason,
            ["offset_ms"] = (long)ring.Offset().TotalMilliseconds,
            ["resume"] = "acceptance resume -root " + opts.Root,
        };
        report["break"] = block;

        // The previous ring is settled before the bundle lands, since a breakpoint it held shares the new bundle's directory.
        IReadOnlyList<Checkpoint> merged = MergeEntries(ring.Entries(), resumed);
        Checkpoint? entry = await ring.BreakBundleAsync(cancellationToken).ConfigureAwait(false);
        List<Checkpoint> entries = [.. ring.Entries()];
        if (entry is not null)
        {
            entries.Add(entry);
        }

        report["checkpoints"] = Rows(entries);
        if (ring.Errors() is { Count: > 0 } errs)
        {
            report["checkpoint_errors"] = Strings(errs);
        }

        if (entry is null)
        {
            block["error"] = "the break bundle could not be taken";
            throw new AcceptanceException($"the break bundle could not be taken: [{string.Join(' ', ring.Errors())}]");
        }

        block["label"] = entry.Label;
        block["path"] = entry.Path;
        block["tick"] = entry.Tick;
        var next = new Ring
        {
            Case = ring.Case,
            SourceRevision = ring.SourceRevision,
            FailedOffsetMs = entry.OffsetMs,
            FailedOutput = ring.Output,
            ResumedFrom = resumed.Entry?.Label ?? "",
            Break = new BreakRecord { Spec = opts.Break.ToString(), Reason = broke.Reason, Tick = entry.Tick, Output = ring.Output, At = entry.At },
        };
        foreach (Checkpoint e in merged)
        {
            next.Entries.Add(e);
        }

        next.Entries.Add(entry);
        next.Next = entry.Label;
        next.Note = string.Create(
            CultureInfo.InvariantCulture,
            $"{c.Name} is paused at its breakpoint {opts.Break} ({broke.Reason}, tick {entry.Tick}); `acceptance resume -root {opts.Root}` continues it, `acceptance stop -root {opts.Root}` discards it");
        try
        {
            next.Write(ring.Dir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            block["error"] = "write ring: " + e.Message;
            throw new AcceptanceException("write ring: " + e.Message, e);
        }

        report["checkpoint_next"] = next.Next;
        report["checkpoint_note"] = next.Note;
    }

    /// <summary>
    /// <see cref="CloseRingAsync"/> for a resumed run whose game never opened on the bundle: the next run steps back one
    /// entry (fresh past the ring) rather than retrying the same bundle forever.
    /// </summary>
    public static void RewindRing(string dir, ResumePlan resumed, Exception openError, Report report)
    {
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(openError);
        ArgumentNullException.ThrowIfNull(report);
        Ring previous = resumed.Previous ?? throw new AcceptanceException("rewind needs the ring the run resumed from");
        string label = resumed.Entry?.Label ?? "";
        var next = new Ring
        {
            Case = previous.Case,
            Failed = previous.Failed,
            FailedTick = previous.FailedTick,
            FailedOffsetMs = previous.FailedOffsetMs,
            ResumedFrom = previous.ResumedFrom,
            SourceRevision = previous.SourceRevision,
            FailedOutput = previous.FailedOutput,
            Break = previous.Break,
        };
        foreach (Checkpoint e in previous.Entries)
        {
            next.Entries.Add(e);
        }

        next.Next = previous.Rewind(1);
        next.Note = next.Next.Length == 0
            ? $"{label} did not open ({openError.Message}) and nothing earlier is in the ring; starting fresh"
            : $"{label} did not open ({openError.Message}); rewinding to {next.Next}";
        try
        {
            next.Write(dir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            report["checkpoint_errors"] = Strings(["write ring: " + e.Message]);
            return;
        }

        report["checkpoint_next"] = next.Next;
        report["checkpoint_note"] = next.Note;
    }

    private static JsonArray Rows(IEnumerable<Checkpoint> entries)
    {
        var rows = new JsonArray();
        foreach (Checkpoint e in entries)
        {
            rows.Add(new JsonObject
            {
                ["label"] = e.Label,
                ["offset_ms"] = e.OffsetMs,
                ["tick"] = e.Tick,
                ["path"] = e.Path,
                ["through"] = e.Through,
                ["wall_ms"] = e.WallMs,
                ["store"] = e.Store,
            });
        }

        return rows;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values)
        {
            array.Add(v);
        }

        return array;
    }
}
