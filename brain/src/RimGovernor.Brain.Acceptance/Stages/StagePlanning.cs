using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Staged run-phase bundles: a case whose Run body plays the colony into its precondition (a shell sited and roofed,
// research and a bench built, rooms on the baseline) declares those stages (AcceptanceCase.Stages) and wraps each
// staging block in StageRun.StageAsync. The first run captures a bundle (save, store, sidecar) after each block into
// <root>/stages/<area>/<case>/<name>/; the next run opens on the newest stage whose fingerprint and staging code still
// match and skips the blocks it covers. A stage is deterministic setup, not the failed attempt: -fresh keeps it,
// -restage discards it, and StagesEnv=0 turns the cache off for a harness whose staging is itself under test.

/// <summary>Stage planning (Go <c>cases/stage.go</c>): the key, the plan, the cached-stage query and the -through check.</summary>
public static class StagePlanning
{
    /// <summary>Set to 0 to disable stage bundles: every block runs and nothing is captured.</summary>
    public const string StagesEnv = "RIMGOVERNOR_ACCEPT_STAGES";

    /// <summary>
    /// The checkpoint state key under which the ring records the last stage a run completed, so a resume from a later
    /// ring bundle skips the stages it already passed.
    /// </summary>
    public const string StageStateKey = "stage_completed";

    /// <summary>Whether <see cref="StagesEnv"/> leaves stage bundles on.</summary>
    public static bool StagesEnabled()
    {
        string v = (Environment.GetEnvironmentVariable(StagesEnv) ?? "").Trim();
        return v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hash a stage bundle of <paramref name="c"/> is keyed on: sha256 over the compiled code of the case's area
    /// (every type in the namespace its Run body is declared in, method bodies in metadata order) plus its sorted
    /// Stages. A change to the staging code invalidates the bundle; neither the git revision nor the rimgovernor build is
    /// in it. The Go key hashed the area package's source files; a compiled assembly carries no source path, so the
    /// intermediate language of the area stands in for it. Throws when the case has no code to hash (staging is then off,
    /// with the reason).
    /// </summary>
    public static string StageKey(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Type? home = c.Run?.Method.DeclaringType;
        if (home is null)
        {
            throw new AcceptanceException("the case carries no code to key the staging on");
        }

        while (home.DeclaringType is { } outer)
        {
            home = outer;
        }

        string area = c.Name.Contains('/', StringComparison.Ordinal) ? c.Name[..c.Name.IndexOf('/', StringComparison.Ordinal)] : c.Name;
        Type[] types;
        try
        {
            types = [.. home.Assembly.GetTypes()
                .Where(t => string.Equals(TopLevel(t).Namespace, home.Namespace, StringComparison.Ordinal))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)];
        }
        catch (ReflectionTypeLoadException e)
        {
            throw new AcceptanceException("the case's area cannot be enumerated: " + e.Message, e);
        }

        if (types.Length == 0)
        {
            throw new AcceptanceException($"no code under {home.Namespace} (area {area})");
        }

        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Type t in types)
        {
            Append(h, t.FullName + "\n");
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodBase m in t.GetConstructors(all).Cast<MethodBase>().Concat(t.GetMethods(all)).OrderBy(m => m.MetadataToken))
            {
                Append(h, m.Name + "\n");
                byte[]? il = m.GetMethodBody()?.GetILAsByteArray();
                if (il is not null)
                {
                    h.AppendData(il);
                }

                h.AppendData([0]);
            }
        }

        string[] stages = [.. c.Stages.Order(StringComparer.Ordinal)];
        Append(h, "stages=" + string.Join(',', stages) + "\n");
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private static Type TopLevel(Type t)
    {
        while (t.DeclaringType is { } outer)
        {
            t = outer;
        }

        return t;
    }

    private static void Append(IncrementalHash h, string text) => h.AppendData(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reads c's stage bundles and picks the last declared stage whose bundle exists and whose fingerprint and stage key
    /// match the current tree, printing its decision on <paramref name="log"/>. -restage discards the bundles first; a
    /// pending ring resume wins over any stage, since it is later on the same timeline.
    /// </summary>
    public static StagePlan PlanStage(AcceptanceCase c, RunOptions opts, ResumePlan resumed, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(log);
        if (c.Stages.Count == 0)
        {
            return StagePlan.None;
        }

        string dir = opts.StagesDir(c);
        if (opts.Restage)
        {
            Checkpoints.DeleteTree(dir);
        }

        if (!StagesEnabled())
        {
            return new StagePlan { Off = StagesEnv + "=0" };
        }

        string key;
        try
        {
            key = StageKey(c);
        }
        catch (AcceptanceException e)
        {
            string off = "cannot key the staging code: " + e.Message;
            log.WriteLine($"stages of {c.Name} off: {off}");
            return new StagePlan { Off = off };
        }

        if (resumed.Resuming)
        {
            // The ring bundle is later on the same timeline; the stages it records as done are skipped, the rest run and capture.
            int hit = -1;
            string done = resumed.Entry?.State?[StageStateKey].AsString() ?? "";
            if (done.Length > 0)
            {
                for (int i = 0; i < c.Stages.Count; i++)
                {
                    if (string.Equals(c.Stages[i], done, StringComparison.Ordinal))
                    {
                        hit = i;
                    }
                }
            }

            return new StagePlan { Key = key, Hit = hit };
        }

        if (opts.Restage)
        {
            return new StagePlan { Key = key };
        }

        Fingerprint current;
        try
        {
            current = RingPlanning.FingerprintOrRoot(c, opts);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"stages of {c.Name} off: cannot fingerprint this root ({e.Message})");
            return new StagePlan { Key = key };
        }

        for (int i = c.Stages.Count - 1; i >= 0; i--)
        {
            string name = c.Stages[i];
            Checkpoint entry;
            try
            {
                entry = Checkpoints.ReadCheckpoint(Path.Combine(dir, name));
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"stage {name} of {c.Name}: {e.Message}; staging again");
                continue;
            }

            string reason = StageMismatch(entry, name, current, key);
            if (reason.Length > 0)
            {
                log.WriteLine($"discarding stage {name} of {c.Name}: {reason}");
                Checkpoints.TryDeleteTree(entry.Path);
                continue;
            }

            log.WriteLine($"opening {c.Name} on stage {name} (captured {entry.At}); -restage stages again");
            return new StagePlan { Key = key, Hit = i, Entry = entry };
        }

        return new StagePlan { Key = key };
    }

    /// <summary>Says why a bundle read from the stage directory name cannot open a run under the current fingerprint and stage key, or "".</summary>
    public static string StageMismatch(Checkpoint entry, string name, Fingerprint current, string key)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(current);
        string reason = current.Mismatch(entry);
        if (reason.Length > 0)
        {
            return reason;
        }

        if (!string.Equals(entry.StageKey, key, StringComparison.Ordinal))
        {
            return "the case's staging code changed";
        }

        return string.Equals(entry.Stage, name, StringComparison.Ordinal) ? "" : "the bundle is not a stage bundle";
    }

    /// <summary>
    /// The newest declared stage of <paramref name="c"/> whose bundle under the root a run would open on (the fingerprint
    /// and stage key match), "" when none: what a suite scheduling stages plans from. It reads only; the run itself
    /// discards what it finds stale.
    /// </summary>
    public static string CachedStage(AcceptanceCase c, string root, bool headless = true)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(root);
        if (c.Stages.Count == 0 || !StagesEnabled())
        {
            return "";
        }

        var opts = new RunOptions { Root = root, Headless = headless };
        try
        {
            string key = StageKey(c);
            Fingerprint current = RingPlanning.FingerprintOrRoot(c, opts);
            for (int i = c.Stages.Count - 1; i >= 0; i--)
            {
                string name = c.Stages[i];
                try
                {
                    Checkpoint entry = Checkpoints.ReadCheckpoint(Path.Combine(opts.StagesDir(c), name));
                    if (StageMismatch(entry, name, current, key).Length == 0)
                    {
                        return name;
                    }
                }
                catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
                {
                    // Unreadable or absent: not cached.
                }
            }
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            return "";
        }

        return "";
    }

    /// <summary>
    /// The case state key as the bundle this run opened on recorded it (a ring resume, else a stage hit; null on a fresh
    /// run): what a staging block wrote through <see cref="Checkpoints.SetState"/> for the code after the stage call,
    /// which on a hit never ran the block. The value is JSON round-tripped, so read it through the tolerant accessors.
    /// </summary>
    public static JsonNode? RestoredState(Checkpoint? resumed, Checkpoint? staged, string key)    {
        if (resumed?.State is { } state && state.TryGetPropertyValue(key, out JsonNode? found))
        {
            return found;
        }

        return staged?.State?[key];
    }

    /// <summary>Validates -through against <paramref name="c"/> before the game opens: a declared stage of a plain run with staging on.</summary>
    public static void CheckThrough(AcceptanceCase c, RunOptions opts)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.Through.Length == 0)
        {
            return;
        }

        if (opts.PostmortemOnly || opts.Dev || opts.Repeat > 1 || !opts.Break.IsZero)
        {
            throw new AcceptanceException("-through takes a plain run: none of -postmortem-only, -repeat, -break, or dev");
        }

        if (!c.Stages.Contains(opts.Through, StringComparer.Ordinal))
        {
            throw new AcceptanceException(c.Stages.Count == 0
                ? $"-through {opts.Through}: case {c.Name} declares no Stages"
                : $"-through {opts.Through}: case {c.Name} declares the stages [{string.Join(' ', c.Stages)}]");
        }

        if (!StagesEnabled())
        {
            throw new AcceptanceException($"-through {opts.Through}: stage bundles are off ({StagesEnv}=0), so nothing would be cached");
        }
    }
}
