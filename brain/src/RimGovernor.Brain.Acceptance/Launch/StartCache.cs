using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What <see cref="Config.PrepareConfig"/> knows and the debug start needs to find and persist the cached start (Go
/// <c>startCache</c> and the cached-start functions of <c>startcache.go</c>): one per harness process, so the shared
/// <see cref="Current"/> rather than a field on every Harness.
/// <para>
/// A debug start normally loads a saved copy of itself instead of generating a world and map every time (a load is about
/// 2.7 s where a generation is far longer). It is on by default (<see cref="CachedStartEnv"/> opts out). The first start
/// under a given map size, planet coverage and expansion set generates through the new-colony op, which saves the result as
/// <c>RimGovernor-debug-&lt;size&gt;-&lt;coverage&gt;[-&lt;expansions&gt;][-&lt;biomes&gt;][-flat][-seed-&lt;seed&gt;]-quiet|loud</c>
/// (the storyteller is part of the save); it is copied into profile/Saves so every later Prepare carries it, and later
/// starts load it. Delete the save to regenerate; a harness that must see a never-before-seen world (world generation
/// itself under test) runs with CachedStartEnv=0. A start with a pinned seed caches as its own save, so it never takes the
/// plain roll's world and a repeat under the seed loads instead of generating.
/// </para>
/// </summary>
public sealed class ProfileStartCache : IStartCache
{
    /// <summary>Controls whether the debug start loads a saved copy of itself; on unless set to 0, false or no.</summary>
    public const string CachedStartEnv = "RIMGOVERNOR_ACCEPT_CACHED_START";

    /// <summary>The longest save name lifecycle_new_colony accepts.</summary>
    public const int MaxSaveName = 64;

    private string _root = "";
    private bool _headless;
    private IReadOnlyList<string> _expansions = [];

    /// <summary>The process-wide cache the prepared profile configures.</summary>
    public static ProfileStartCache Current { get; } = new();

    /// <summary>Whether the saved start is used: true unless <see cref="CachedStartEnv"/> opts out.</summary>
    public static bool CachedStart() => !Game.EnvOptsOut(CachedStartEnv);

    public bool Enabled => CachedStart() && _root.Length > 0;

    public string Root => _root;

    public IReadOnlyList<string> Expansions => _expansions;

    /// <summary>The seed the last debug start generated with, empty when it loaded the cached save.</summary>
    public string Seed { get; set; } = "";

    /// <summary>The cached save the last debug start loaded, empty when it generated.</summary>
    public string Save { get; set; } = "";

    /// <summary>Records the prepared profile (what <see cref="Config.PrepareConfig"/> does with its result).</summary>
    public void Configure(string root, bool headless, IReadOnlyList<string> expansions)
    {
        ArgumentNullException.ThrowIfNull(expansions);
        _root = root;
        _headless = headless;
        _expansions = [.. expansions];
    }

    /// <summary>
    /// Names the save a start caches as. The storyteller is baked into the save by the op, so a quiet and a loud (ordinary)
    /// start never share one.
    /// </summary>
    public string NameFor(DebugStartSize start, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(start);
        var name = new StringBuilder("RimGovernor-debug-")
            .Append(start.MapSize.ToString(CultureInfo.InvariantCulture))
            .Append('-')
            .Append(start.PlanetCoverage.ToString(CultureInfo.InvariantCulture))
            .ToString();
        if (_expansions.Count > 0)
        {
            name += "-" + string.Join('-', _expansions).ToLowerInvariant();
        }

        // A biome preference is part of what the save satisfies: a start pinned to a food-bearing biome must not load a
        // plain roll's save.
        if (start.Biomes.Length > 0)
        {
            name += "-" + start.Biomes.Replace(" ", "", StringComparison.Ordinal).Replace(',', '-').ToLowerInvariant();
        }

        if (start.Flat)
        {
            name += "-flat";
        }

        if (start.Seed.Length > 0)
        {
            name += "-seed-" + SeedToken(start.Seed);
        }

        name += quiet ? "-quiet" : "-loud";
        name = name.Replace('.', '_');
        if (name.Length > MaxSaveName)
        {
            // The op's save names are at most 64 characters: keep a readable prefix and make the whole name's hash the tail.
            var sum = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            name = name[..(MaxSaveName - 9)] + "-" + Convert.ToHexStringLower(sum.AsSpan(0, 4));
        }

        return name;
    }

    /// <summary>Where the running process's profile would hold the save, and whether it is there.</summary>
    public bool TryGetPath(string name, out string path)
    {
        path = Path.Combine(_root, _headless ? "headless-profile" : "profile", "Saves", name + ".rws");
        return File.Exists(path);
    }

    /// <summary>
    /// Checks whether the cached save requires different expansions from the active profile. A mismatch prevents loading
    /// (save.missing_mods) and requires regeneration.
    /// </summary>
    public bool IsStale(string path, string name)
    {
        IReadOnlyList<string> recorded;
        try
        {
            recorded = HeadlessProfile.SaveExpansionsAt(path, name);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"cached start {name}: {e.Message}", e);
        }

        var wanted = _expansions.Select(NormalizeExpansion).Order(StringComparer.Ordinal);
        var have = recorded.Order(StringComparer.Ordinal);
        return !string.Equals(string.Join(',', wanted), string.Join(',', have), StringComparison.Ordinal);
    }

    private static string NormalizeExpansion(string name) => Brain.Acceptance.Expansions.ExpansionPackage(name);

    /// <summary>Loads the save and waits for it the way a quick start is waited for.</summary>
    public async Task LoadAsync(Harness harness, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        try
        {
            await harness.CallAsync(
                "cached-start-load",
                "rimgovernor/load_game_ready",
                new JsonObject
                {
                    ["saveName"] = name,
                    ["readiness"] = "visual",
                    ["timeoutMs"] = 90000,
                    ["ignoreModCompatibility"] = false,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"cached start {name}: {e.Message}", e);
        }
    }

    /// <summary>Writes the just-started game as <paramref name="name"/> and copies it into profile/Saves, the durable location Prepare mirrors from.</summary>
    public async Task SaveAsync(Harness harness, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var identityReply = await harness.WireAsync("cached-start-identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(identityReply, "loaded");
        var loadedContext = loaded["context"].AsMap();
        var identity = loadedContext?["identity"].AsMap();
        var tick = loadedContext?["tick"].AsNumber() ?? -1;
        var nanos = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        JsonObject saveReply;
        try
        {
            saveReply = await harness.WireAsync(
                "cached-start-save",
                "lifecycle_save",
                new JsonObject
                {
                    ["player"] = new JsonObject
                    {
                        ["identity"] = identity?.DeepClone(),
                        ["playerDirection"] = 1,
                        ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"cached-start-{nanos}"),
                    },
                    ["saveName"] = name,
                    ["expectedTick"] = tick,
                },
                cancellationToken).ConfigureAwait(false);
            _ = Harness.Outcome(saveReply, "completed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"lifecycle_save: {e.Message}", e);
        }

        if (!TryGetPath(name, out var source))
        {
            throw new AcceptanceException($"lifecycle_save completed but {source} is not there");
        }

        var destination = Path.Combine(_root, "profile", "Saves", name + ".rws");
        if (!string.Equals(source, destination, StringComparison.Ordinal))
        {
            HeadlessProfile.CopyFile(source, destination);
        }
    }

    /// <summary>
    /// Copies the save the running process just wrote from its own (possibly disposable) profile directory into
    /// profile/Saves, the durable location every harness's Prepare reads from.
    /// </summary>
    public void PersistSave(string name) => Baselines.PersistSave(_root, _headless, name);

    /// <summary>Draws a world seed for a debug start nothing pinned: ten lowercase letters, a save-name-safe token the record and a later -seed can carry verbatim.</summary>
    public string RandomSeed() => RandomToken();

    private static string RandomToken()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        var raw = RandomNumberGenerator.GetBytes(10);
        return string.Create(raw.Length, raw, static (span, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                span[i] = letters[bytes[i] % letters.Length];
            }
        });
    }

    /// <summary>The seed as a save-name fragment: lowercase letters and digits only.</summary>
    public static string SeedToken(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var token = new StringBuilder();
        foreach (var c in seed.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                token.Append(c);
            }
        }

        return token.ToString();
    }
}
