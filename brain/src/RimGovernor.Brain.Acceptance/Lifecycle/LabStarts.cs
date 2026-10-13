using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The blank lab start (Go <c>labstart.go</c>; the value type is <see cref="LabStart"/>): a fixed-seed
/// <see cref="LabWorld.LabMapSize"/> map wiped to Soil with fixture-made colonists, clear weather, a pinned
/// temperature and a quiet storyteller. It caches as <c>RimGovernor-lab-&lt;size&gt;[-&lt;expansions&gt;][-c&lt;n&gt;]</c>
/// beside a .stamp file holding the installed mod's hash, so a mod or fixture rebuild regenerates it; a later start
/// pays only the load.
/// </summary>
public static class LabStarts
{
    /// <summary>The colonists a lab start makes: zero or less is <see cref="LabWorld.DefaultLabColonist"/>.</summary>
    public static int Colonists(LabStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return start.Colonists <= 0 ? LabWorld.DefaultLabColonist : start.Colonists;
    }

    /// <summary>The debug start the lab is generated on before the fixture wipes it.</summary>
    public static DebugStartSize Base() => new()
    {
        MapSize = LabWorld.LabMapSize,
        PlanetCoverage = DebugStartSize.DefaultPlanetCoverage,
        Seed = LabWorld.LabSeed,
        Biomes = LabWorld.LabBiome,
    };

    /// <summary>What the lab contributes to the report's world block.</summary>
    public static WorldSource World(LabStart start, IStartCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var source = new WorldSource { Seed = LabWorld.LabSeed, Pinned = true };
        var name = LabCacheName(cache, Colonists(start));
        return cache.Enabled && cache.TryGetPath(name, out _) ? source with { Save = name } : source;
    }

    /// <summary>The lab save's profile name.</summary>
    public static string LabCacheName(IStartCache cache, int colonists)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var name = string.Create(CultureInfo.InvariantCulture, $"RimGovernor-lab-{LabWorld.LabMapSize}");
        if (cache.Expansions.Count > 0)
        {
            name += "-" + string.Join('-', cache.Expansions).ToLowerInvariant();
        }

        if (colonists != LabWorld.DefaultLabColonist)
        {
            name += string.Create(CultureInfo.InvariantCulture, $"-c{colonists}");
        }

        return name.Replace('.', '_');
    }

    /// <summary>The hash over the installed package's files (the report's package_files), the cache key a rebuild of the mod or its fixtures changes.</summary>
    public static string LabStamp(JsonObject files)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var key in files.Select(pair => pair.Key).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(key.Replace('\\', '/') + "=" + files[key].AsString() + "\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Loads or generates the lab on the harness's game and returns the report row for "start". A fresh cached save is
    /// loaded and its recorded lab reply replayed; otherwise the lab is generated, wiped by the fixture and, when the
    /// cache is on, saved with its reply and stamp.
    /// </summary>
    public static async Task<JsonObject> LoadAsync(LabStart start, Session session, QuietMode quiet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(session);
        _ = quiet;
        var harness = session.Harness;
        var cache = session.Host.StartCache;
        if (!Session.HasTool(session.Names, DebugGame.LabStartTool))
        {
            throw new AcceptanceException($"{DebugGame.LabStartTool} not in discovery: rebuild the native mod with any -Fixture flag (every fixture build includes DebugStartFixture)");
        }

        var colonists = Colonists(start);
        var name = LabCacheName(cache, colonists);
        var files = session.Report["package_files"].AsMap() ?? [];
        var stamp = LabStamp(files);
        var cached = cache.Enabled;
        cache.Seed = "";
        var row = new JsonObject { ["kind"] = "lab", ["mapSize"] = LabWorld.LabMapSize, ["colonists"] = colonists, ["save"] = name };
        var fresh = cached && LabCacheFresh(cache, name, stamp);
        if (fresh)
        {
            await cache.LoadAsync(harness, name, cancellationToken).ConfigureAwait(false);
            JsonObject lab;
            try
            {
                lab = Dyn.ParseObject(await File.ReadAllTextAsync(LabReplyPath(cache, name), cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                throw new AcceptanceException($"lab cache {name}: {e.Message}", e);
            }

            row["lab"] = lab;
            row["cached"] = true;
        }
        else
        {
            await DebugGame.GenerateDebugStartAsync(harness, cache, Base(), quiet: true, name, cancellationToken).ConfigureAwait(false);
            JsonObject lab;
            try
            {
                lab = await harness.CallAsync("lab-start", DebugGame.LabStartTool, new JsonObject { ["colonists"] = colonists }, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{DebugGame.LabStartTool}: {e.Message}", e);
            }

            if (lab["success"].AsBool() != true)
            {
                throw new AcceptanceException($"{DebugGame.LabStartTool} refused: {lab.ToJson()}");
            }

            var size = (int)lab["mapSize"].AsNumber();
            if (size != LabWorld.LabMapSize)
            {
                throw new AcceptanceException($"{DebugGame.LabStartTool} made a {size} map, want {LabWorld.LabMapSize}");
            }

            row["digest"] = lab["digest"]?.DeepClone();
            row["lab"] = lab;
            row["cached"] = false;
            if (cached)
            {
                await cache.SaveAsync(harness, name, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(LabReplyPath(cache, name), lab.ToJsonString(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(LabStampPath(cache, name), stamp + "\n", cancellationToken).ConfigureAwait(false);
            }
        }

        // The lab is quiet by construction and the save persists it; applying again marks the harness quiet and
        // covers a lab made before a change to the quiet op.
        await DebugGame.ApplyQuietAsync(harness, Session.HasTool(session.Names, DebugGame.QuietStorytellerTool), "apply", cancellationToken).ConfigureAwait(false);
        session.Report["quiet"] = HarnessQuiet.Get(harness);
        return row;
    }

    /// <summary>
    /// Loads the default lab into an Owned case's own game: the same cached save and stamp a Lab start uses.
    /// <paramref name="host"/> supplies the package hashes and the start cache.
    /// </summary>
    public static async Task<JsonObject> StartLabAsync(Config config, ISessionHost host, Harness harness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var files = host.PackageFiles(config);
        var report = new Report();
        report["package_files"] = files;
        var session = await Session.AdoptAsync(config, host, harness, report, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(new LabStart(), session, QuietMode.Required, cancellationToken).ConfigureAwait(false);
    }

    private static string SavesDir(IStartCache cache) => Path.Combine(cache.Root, "profile", "Saves");

    private static string LabStampPath(IStartCache cache, string name) => Path.Combine(SavesDir(cache), name + ".stamp");

    /// <summary>The test/lab_start reply (centre, colonist ids) the cached save was made with: a cached load replies it as the lab start's Prepared.</summary>
    private static string LabReplyPath(IStartCache cache, string name) => Path.Combine(SavesDir(cache), name + ".lab.json");

    /// <summary>Whether the cached lab save exists, was recorded under the profile's expansions and carries <paramref name="stamp"/>.</summary>
    private static bool LabCacheFresh(IStartCache cache, string name, string stamp)
    {
        if (!cache.TryGetPath(name, out var path) || !File.Exists(LabReplyPath(cache, name)))
        {
            return false;
        }

        try
        {
            if (File.ReadAllText(LabStampPath(cache, name)).Trim() != stamp)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }

        return !cache.IsStale(path, name);
    }
}
