using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The saves the harness generates itself on first use and regenerates when their spec changes (Go <c>baseline.go</c>):
/// the tribal-8 baseline most save-driven harnesses begin from, and any part-bearing start a case registers.
/// </summary>
public static class Baselines
{
    /// <summary>The starting colony most save-driven harnesses begin from (profile/Saves/BaselineSave).</summary>
    public const string BaselineSave = "RimGovernor-tribal8-baseline.rws";

    /// <summary><see cref="BaselineSave"/> without the extension: the save name the op writes and Save starts load.</summary>
    public const string BaselineName = "RimGovernor-tribal8-baseline";

    /// <summary>The file beside the save that records which spec generated it, so a changed start regenerates the save.</summary>
    public const string StampSuffix = ".spec.json";

    private static readonly JsonSerializerOptions StampOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lock Gate = new();

    /// <summary>
    /// The tribal-8 spec the baseline is generated from: eight LostTribe colonists in a TemperateForest, Medium
    /// difficulty, a little colder world, 250-cell map on a 0.3 planet. The seed pins the world; the team policy's
    /// rerolls decide the colonists.
    /// </summary>
    public static readonly ScenarioSpec BaselineStart = new()
    {
        Scenario = "LostTribe",
        Count = 8,
        Seed = "rimgovernor-tribal-eight-e",
        Biome = "TemperateForest",
        Difficulty = "Medium",
        WorldTemperature = "LittleBitColder",
        Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.3 },
        SaveName = BaselineName,
    };

    /// <summary>The starts whose saves are generated and stamped, by save name: the baseline, and any a case registers.</summary>
    private static readonly Dictionary<string, ScenarioSpec> StampedStarts = new(StringComparer.Ordinal) { [BaselineName] = BaselineStart };

    /// <summary>The save names of the stamped starts, in ordinal order.</summary>
    public static IReadOnlyList<string> StampedNames()
    {
        lock (Gate)
        {
            return [.. StampedStarts.Keys.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Makes the start's save (<see cref="ScenarioSpec.SaveName"/>) a generated, stamped save like the baseline:
    /// <see cref="EnsureSaveAsync"/> generates it in profile/Saves on first use and again whenever the spec changes.
    /// The start needs a seed and a save name; registering one name twice is a programming error.
    /// </summary>
    public static void RegisterStampedStart(ScenarioSpec start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.SaveName.Length == 0 || start.Seed.Length == 0)
        {
            throw new ArgumentException("a stamped start needs a SaveName and a Seed", nameof(start));
        }

        lock (Gate)
        {
            if (!StampedStarts.TryAdd(start.SaveName, start))
            {
                throw new InvalidOperationException($"stamped start {start.SaveName} registered twice");
            }
        }
    }

    public static string StartSavePath(string root, string name) =>
        Path.Combine(HeadlessProfile.MustAbs(root), "profile", "Saves", name + ".rws");

    public static string StartStampPath(string root, string name) =>
        Path.Combine(HeadlessProfile.MustAbs(root), "profile", "Saves", name + StampSuffix);

    /// <summary>The stamp the start would write now: the op's wire spec, canonically ordered and indented.</summary>
    private static string StartStamp(ScenarioSpec start)
    {
        var spec = ScenarioStarts.Spec(start, ScenarioStarts.QuietStorytellerDef);
        var ordered = JsonNode.Parse(Dyn.Canonical(spec));
        return (ordered?.ToJsonString(StampOptions) ?? "null").Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="root"/> holds the start's save generated from its current spec: the save exists and its
    /// stamp matches. A save with no stamp (hand-staged, or an older spec) is stale.
    /// </summary>
    public static bool StartCurrent(string root, ScenarioSpec start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!File.Exists(StartSavePath(root, start.SaveName)))
        {
            return false;
        }

        var want = StartStamp(start);
        string have;
        try
        {
            have = File.ReadAllText(StartStampPath(root, start.SaveName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return string.Equals(have.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), want.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Records that the root's save for the start was generated from its current spec.</summary>
    public static void StampStart(string root, ScenarioSpec start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var want = StartStamp(start);
        try
        {
            File.WriteAllText(StartStampPath(root, start.SaveName), want + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"write stamp for {start.SaveName}: {e.Message}", e);
        }
    }

    /// <summary><see cref="StartCurrent"/> for <see cref="BaselineStart"/>.</summary>
    public static bool BaselineCurrent(string root) => StartCurrent(root, BaselineStart);

    /// <summary><see cref="StampStart"/> for <see cref="BaselineStart"/>.</summary>
    public static void StampBaseline(string root) => StampStart(root, BaselineStart);

    /// <summary>
    /// Generates a save the harness owns when <paramref name="config"/>'s root lacks a current copy: the tribal-8
    /// baseline or a registered stamped start, once per root, on first use. Any other name is left to the caller.
    /// </summary>
    public static async Task EnsureSaveAsync(Config config, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ScenarioSpec? start;
        lock (Gate)
        {
            _ = StampedStarts.TryGetValue(name.EndsWith(".rws", StringComparison.Ordinal) ? name[..^4] : name, out start);
        }

        if (start is null || StartCurrent(config.Root, start))
        {
            return;
        }

        try
        {
            await GenerateStartSaveAsync(config, start, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"generate {start.SaveName}: {e.Message}", e);
        }

        StampStart(config.Root, start);
    }

    /// <summary>
    /// Starts the start through the new-colony op on a Core-only profile (a stamped save's recorded mods pin every later
    /// run to Core), copies the save into profile/Saves and stops the game: its mod list differs from the run that asked
    /// for it.
    /// </summary>
    private static async Task GenerateStartSaveAsync(Config config, ScenarioSpec start, CancellationToken cancellationToken)
    {
        var gameId = config.GameId.Length > 0 ? config.GameId : HeadlessProfile.TrialGameId;
        var generation = new Config
        {
            Root = config.Root,
            Output = Path.Combine(HeadlessProfile.MustAbs(config.Root), "acceptance", "baseline-generate", start.SaveName),
            Headless = config.Headless,
            GameId = gameId,
            Timeout = config.Timeout,
            Expansions = [],
        };
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            var session = await Session.OpenAsync(
                generation,
                SessionHost.Default,
                new Report(),
                new ScenarioStart { Spec = start },
                QuietMode.IfAvailable,
                [NeedDef.LiveNeeds],
                bound.Token).ConfigureAwait(false);
            session.Close();
        }
        finally
        {
            // The generating game's mod list differs from the run that asked for it, so it never stays up; this is also
            // what ends a game left behind by a failed open.
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Game.StopGameAsync(generation.Root, gameId, stop.Token).ConfigureAwait(false);
        }

        PersistSave(config.Root, config.Headless, start.SaveName);
    }

    /// <summary>
    /// Copies the save the running process just wrote from its own (possibly disposable) profile directory into
    /// profile/Saves, the durable location every harness's Prepare reads from. In rendered mode the running profile
    /// already is profile/Saves; in headless mode it is headless-profile/Saves, which Prepare overwrites from
    /// profile/Saves on every run.
    /// </summary>
    public static void PersistSave(string root, bool headless, string save)
    {
        var abs = HeadlessProfile.MustAbs(root);
        var destination = Path.Combine(abs, "profile", "Saves", save + ".rws");
        var source = Path.Combine(abs, headless ? "headless-profile" : "profile", "Saves", save + ".rws");
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"persist save {save}: {e.Message}", e);
        }
    }
}

public sealed partial class Config
{
    /// <summary>
    /// Generates a save the harness owns when the root lacks a current copy (the tribal-8 baseline or a registered
    /// stamped start), once per root, on first use. Any other name is left to the caller.
    /// </summary>
    public Task EnsureSaveAsync(string name, CancellationToken cancellationToken) => Baselines.EnsureSaveAsync(this, name, cancellationToken);
}
