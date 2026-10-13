using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// A checkpoint bundle is one directory holding everything a later run needs to resume a case where this one was:
// the game save (CheckpointSaveName + ".rws"), the service's durable state (CheckpointStoreFile, serve-driven cases
// only) and the CheckpointSidecar describing them.

/// <summary>
/// The checkpoint constants and the process-wide hooks (Go package-level functions of <c>checkpoint.go</c>): one case
/// runs per process at a time, so a single ring is active and the hooks (<c>Harness.CallAsync</c>, the wait probes,
/// a case's own watch) consult it.
/// </summary>
public static class Checkpoints
{
    /// <summary>The ring's cadence in run phase.</summary>
    public static readonly TimeSpan DefaultCheckpointEvery = TimeSpan.FromMinutes(1);

    /// <summary>How many periodic entries a ring retains.</summary>
    public const int CheckpointKeep = 5;

    /// <summary>The bundle's description.</summary>
    public const string CheckpointSidecar = "checkpoint.json";

    /// <summary>The bundle's copy of the service's SQLite state.</summary>
    public const string CheckpointStoreFile = "service.sqlite";

    /// <summary>The ring's index (<see cref="Ring"/>), beside its bundles.</summary>
    public const string CheckpointRingFile = "ring.json";

    /// <summary>
    /// The label of the bundle taken as a failed run ends, after the periodic ring; <see cref="Breakpoint.BreakCheckpoint"/>
    /// is its counterpart for a run paused at a breakpoint.
    /// </summary>
    public const string FailedCheckpoint = "failed";

    private static CheckpointRing? _active;

    /// <summary>The ring the hooks consult, null when none is active.</summary>
    public static CheckpointRing? Active => Volatile.Read(ref _active);

    internal static void Install(CheckpointRing ring) => Volatile.Write(ref _active, ring);

    internal static void Uninstall(CheckpointRing ring) => Interlocked.CompareExchange(ref _active, null, ring);

    /// <summary>
    /// The hook the natural-pause sites call (Go <c>CheckpointPause</c>, also a case's own poll loop): captures a ring
    /// entry when one is due and returns how long that took, so a wait can leave it out of its stall accounting.
    /// </summary>
    public static Task<TimeSpan> PauseAsync(CancellationToken cancellationToken)
    {
        var ring = Active;
        return ring is null || ring.Every <= TimeSpan.Zero ? Task.FromResult(TimeSpan.Zero) : ring.MaybeAsync(cancellationToken);
    }

    /// <summary>
    /// Takes a named bundle on the active ring (a phase boundary the case declared: a goal admitted, a stage
    /// advanced); null when no ring is active.
    /// </summary>
    public static async Task<Checkpoint?> CaptureCheckpointAsync(string label, CancellationToken cancellationToken)
    {
        var ring = Active;
        return ring is null ? null : await ring.CaptureAsync(label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records key on the active ring's case state, carried by every later capture (a phase the Run body completed,
    /// the fixture coordinates it chose); a null value deletes the key. Nothing happens when no ring is active.
    /// </summary>
    public static void SetState(string key, JsonNode? value) => Active?.SetState(key, value);

    /// <summary>
    /// Stops the active ring taking any further entry, periodic or named, so a resume of a later failure replays from
    /// the last entry before the cap: a case calls it at a point of no return its Run body cannot resume after
    /// (defense/perimeter at its raid, whose sprung traps fail the pre-raid audit a resume replays). The failed bundle
    /// is still taken. <paramref name="reason"/> goes on the report. Nothing happens when no ring is active.
    /// </summary>
    public static void Cap(string reason) => Active?.CapWith(reason);

    /// <summary>Reads the sidecar of the bundle at dir, with Path set.</summary>
    public static Checkpoint ReadCheckpoint(string dir) => Ring.ReadCheckpoint(dir);

    /// <summary>Reads dir's index; a missing ring is null.</summary>
    public static Ring? ReadRing(string dir) => Ring.Read(dir);

    /// <summary>
    /// <see cref="Fingerprint.Start"/> over a Start's description (Describe output: kind, save name, fixture op and
    /// args, nested start).
    /// </summary>
    public static string StartHash(JsonObject described) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Dyn.Canonical(described))));

    /// <summary>
    /// <see cref="Fingerprint.Package"/> over the files of the package installed under the game (name to content
    /// hash; the setup slice's PackageFiles), "" when there are none or they could not be listed.
    /// </summary>
    public static string PackageHash(IReadOnlyDictionary<string, string>? files)
    {
        if (files is null || files.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder();
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(files[name]).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The save name a case's bundles use, stable per case so the game's profile holds one such save at a time.
    /// </summary>
    public static string CheckpointSaveName(string caseName) =>
        "RimGovernor-checkpoint-" + (caseName ?? throw new ArgumentNullException(nameof(caseName))).Replace('/', '-');

    /// <summary>Copies the regular files under src into dst, recreating the directory layout.</summary>
    public static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var directory in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
        }
    }

    /// <summary>
    /// Copies the bundle's save into root/profile/Saves (the durable location Prepare mirrors into the game's
    /// profile), replacing any earlier copy, and the bundle's store to <paramref name="storePath"/> when it carries
    /// one. It returns the save name the resumed session loads.
    /// </summary>
    public static string StageCheckpoint(string root, Checkpoint c, string storePath)
    {
        ArgumentNullException.ThrowIfNull(c);
        var src = Path.Combine(c.Path, c.Save + ".rws");
        if (!File.Exists(src))
        {
            throw new AcceptanceException($"checkpoint {c.Label}: {src} is missing");
        }

        CopyFile(src, Path.Combine(root, "profile", "Saves", c.Save + ".rws"));
        if (c.Store && storePath.Length > 0)
        {
            try
            {
                CopyFile(Path.Combine(c.Path, CheckpointStoreFile), storePath);
            }
            catch (IOException e)
            {
                throw new AcceptanceException($"checkpoint {c.Label} store: {e.Message}", e);
            }
        }

        return c.Save;
    }

    /// <summary>Copies one file, creating the destination directory and replacing any earlier copy.</summary>
    internal static void CopyFile(string src, string dst)
    {
        var parent = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>Removes a directory tree; a missing one is fine.</summary>
    internal static void DeleteTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Removes a directory tree, ignoring failures (a prune must not fail a capture).</summary>
    internal static void TryDeleteTree(string path)
    {
        try
        {
            DeleteTree(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover directory is overwritten by the next capture of that label.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

public sealed partial class Config
{
    /// <summary>
    /// Where the running game writes a save named <paramref name="name"/>: the headless profile under Prepare, the
    /// windowed one under PrepareRendered.
    /// </summary>
    public string ProfileSave(string name) =>
        Path.Combine(Root, Headless ? "headless-profile" : "profile", "Saves", name + ".rws");
}
