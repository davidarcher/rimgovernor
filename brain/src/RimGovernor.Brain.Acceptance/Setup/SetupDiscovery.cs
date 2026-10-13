using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The machine's shared game files the setup reads from and never writes (Go <c>setup.Inputs</c>): the RimWorld install
/// the copy junctions into and the Harmony assembly the mod build references.
/// </summary>
public sealed class SetupInputs
{
    public SetupInputs(string rimWorldDir, string harmony, string harmonyMod, bool cleanProfile, IReadOnlyDictionary<string, string> sources)
    {
        RimWorldDir = rimWorldDir;
        Harmony = harmony;
        HarmonyMod = harmonyMod;
        CleanProfile = cleanProfile;
        Sources = sources;
    }

    /// <summary>The Harmony runtime mod directory; empty unless explicit.</summary>
    public string HarmonyMod { get; }

    /// <summary>Prevents importing any machine-local player preferences.</summary>
    public bool CleanProfile { get; }

    /// <summary>The directory holding RimWorld.exe.</summary>
    public string RimWorldDir { get; }

    /// <summary>0Harmony.dll.</summary>
    public string Harmony { get; }

    /// <summary>How each input was found (keys <c>rimworld</c> and <c>harmony</c>), for the summary.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; }
}

/// <summary>The explicit paths the caller gives; empty fields are discovered (Go <c>setup.Overrides</c>).</summary>
public sealed class SetupOverrides
{
    public string HarmonyMod { get; set; } = "";

    /// <summary>Requires all dependency paths; no Steam or peer discovery occurs.</summary>
    public bool Explicit { get; set; }

    public string RimWorldDir { get; set; } = "";

    public string Harmony { get; set; } = "";

    /// <summary>The worktree being set up.</summary>
    public string Repo { get; set; } = "";
}

/// <summary>A failure of the setup (a missing input, a refusal, a failed build step); the message is the user-facing text.</summary>
public sealed class SetupException : AcceptanceException
{
    public SetupException(string message)
        : base(message)
    {
    }

    public SetupException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Resolves the machine's RimWorld and Harmony inputs (Go <c>setup/discover.go</c>): each override, then its
/// environment variable, then Steam (the registry's install path and every library in
/// <c>steamapps/libraryfolders.vdf</c>).
/// </summary>
public static partial class SetupDiscovery
{
    /// <summary>Names the RimWorld install (the directory holding RimWorld.exe); overrides discovery.</summary>
    public const string RimWorldDirEnv = "RIMGOVERNOR_RIMWORLD_DIR";

    /// <summary>Names 0Harmony.dll; overrides discovery.</summary>
    public const string HarmonyEnv = "RIMGOVERNOR_HARMONY_DLL";

    /// <summary>RimWorld's Steam app id: its workshop content lives under steamapps/workshop/content/&lt;id&gt;.</summary>
    public const string RimWorldAppId = "294100";

    /// <summary>The Harmony mod's workshop item id.</summary>
    public const string HarmonyWorkshopId = "2009463077";

    /// <summary>The game's executable and the install marker: the flat 1.7 layout has no RimWorldWin64.exe, _Data or MonoBleedingEdge.</summary>
    public const string GameImage = "RimWorld.exe";

    /// <summary>Where the Harmony mod keeps its 1.7 (net10) build, relative to the mod root.</summary>
    public static readonly string HarmonyAssemblies = Path.Combine("1.7", "Assemblies");

    /// <summary>
    /// Resolves the inputs: each override, then its environment variable, then Steam for the game and Harmony.
    /// Throws <see cref="SetupException"/> when one cannot be found.
    /// </summary>
    public static SetupInputs Discover(SetupOverrides o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<string> libraries = [];
        string? libErr = null;
        var cleanProfile = false;
        if (o.Explicit)
        {
            if (o.RimWorldDir.Length == 0 || o.Harmony.Length == 0 || o.HarmonyMod.Length == 0)
            {
                throw new SetupException("explicit setup requires -rimworld, -harmony and -harmony-mod");
            }

            cleanProfile = true;
            if (!IsFile(Path.Combine(o.HarmonyMod, "About", "About.xml")) || !IsFile(Path.Combine(o.HarmonyMod, HarmonyAssemblies, "HarmonyMod.dll")))
            {
                throw new SetupException("explicit setup requires the complete Harmony runtime mod, not only 0Harmony.dll");
            }
        }
        else
        {
            try
            {
                libraries = SteamLibraries();
            }
            catch (SetupException e)
            {
                libErr = e.Message;
            }
        }

        var dir = o.RimWorldDir;
        var source = "-rimworld";
        if (dir.Length == 0)
        {
            dir = Environment.GetEnvironmentVariable(RimWorldDirEnv) ?? "";
            source = RimWorldDirEnv;
        }

        if (dir.Length == 0)
        {
            foreach (var lib in libraries)
            {
                var candidate = Path.Combine(lib, "steamapps", "common", "RimWorld");
                if (IsFile(Path.Combine(candidate, GameImage)))
                {
                    dir = candidate;
                    source = "steam library " + lib;
                    break;
                }
            }
        }

        if (dir.Length == 0)
        {
            if (libErr is not null)
            {
                throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"no RimWorld install: {libErr}; set {RimWorldDirEnv} or pass -rimworld"));
            }

            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"no RimWorld install in the Steam libraries [{string.Join(' ', libraries)}]; set {RimWorldDirEnv} or pass -rimworld"));
        }

        dir = AbsClean(dir);
        if (!IsFile(Path.Combine(dir, GameImage)))
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"{dir} ({source}) holds no {GameImage}"));
        }

        sources["rimworld"] = source;
        if (!IsFile(Path.Combine(dir, "Assembly-CSharp.dll")))
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"{dir} holds no Assembly-CSharp.dll"));
        }

        var harmony = o.Harmony;
        source = "-harmony";
        if (harmony.Length == 0)
        {
            harmony = Environment.GetEnvironmentVariable(HarmonyEnv) ?? "";
            source = HarmonyEnv;
        }

        if (harmony.Length == 0)
        {
            foreach (var candidate in HarmonyCandidates(dir, libraries))
            {
                if (IsFile(candidate))
                {
                    harmony = candidate;
                    source = "steam workshop";
                    break;
                }
            }
        }

        if (harmony.Length == 0)
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"no 0Harmony.dll: subscribe to Harmony (workshop item {HarmonyWorkshopId}) or set {HarmonyEnv}"));
        }

        harmony = AbsClean(harmony);
        if (!IsFile(harmony))
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"{harmony} ({source}) is not a file"));
        }

        if (o.Explicit)
        {
            var runtimeDll = Path.Combine(o.HarmonyMod, HarmonyAssemblies, "0Harmony.dll");
            if (!IsFile(runtimeDll))
            {
                throw new SetupException("harmony runtime DLL: " + runtimeDll + " does not exist");
            }

            // Go os.SameFile: the same file by identity. Paths (links resolved) stand in for it.
            if (!string.Equals(ResolvedFile(harmony), ResolvedFile(runtimeDll), StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"-harmony must name the DLL inside -harmony-mod/{HarmonyAssemblies}"));
            }
        }

        sources["harmony"] = source;
        return new SetupInputs(dir, harmony, o.HarmonyMod, cleanProfile, sources);
    }

    /// <summary>
    /// Where a Steam Harmony lives: the workshop item in any library (the game's own library first), then a copy under
    /// the game's Mods.
    /// </summary>
    private static List<string> HarmonyCandidates(string rimworld, IReadOnlyList<string> libraries)
    {
        var tail = Path.Combine("steamapps", "workshop", "content", RimWorldAppId, HarmonyWorkshopId, HarmonyAssemblies, "0Harmony.dll");
        var output = new List<string>();
        var parent = Path.GetDirectoryName(rimworld);
        if (parent is not null && string.Equals(Path.GetFileName(parent), "common", StringComparison.Ordinal))
        {
            var own = Path.GetDirectoryName(Path.GetDirectoryName(parent));
            if (own is not null)
            {
                output.Add(Path.Combine(own, tail));
            }
        }

        foreach (var lib in libraries)
        {
            output.Add(Path.Combine(lib, tail));
        }

        output.Add(Path.Combine(rimworld, "Mods", "Harmony", HarmonyAssemblies, "0Harmony.dll"));
        return output;
    }

    /// <summary>
    /// Lists the Steam library roots on this machine: the install directory from the registry (or the conventional
    /// Program Files location), plus every path in its <c>steamapps/libraryfolders.vdf</c>.
    /// </summary>
    public static IReadOnlyList<string> SteamLibraries()
    {
        var roots = new List<string>();
        var installed = SteamInstallPath();
        if (installed.Length > 0)
        {
            roots.Add(installed);
        }

        foreach (var conventional in new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "Steam"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Steam"),
        })
        {
            if (IsDir(conventional))
            {
                roots.Add(conventional);
            }
        }

        if (roots.Count == 0)
        {
            throw new SetupException("steam is not installed (no registry install path, no Program Files\\Steam)");
        }

        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string p)
        {
            p = AbsClean(p);
            if (IsDir(p) && seen.Add(p))
            {
                output.Add(p);
            }
        }

        foreach (var root in roots)
        {
            Add(root);
            string data;
            try
            {
                data = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var p in LibraryPaths(data))
            {
                Add(p);
            }
        }

        return output;
    }

    /// <summary>Extracts the "path" values of a libraryfolders.vdf, with its doubled backslashes undone.</summary>
    public static IReadOnlyList<string> LibraryPaths(string vdf)
    {
        ArgumentNullException.ThrowIfNull(vdf);
        var output = new List<string>();
        foreach (Match m in VdfPath().Matches(vdf))
        {
            output.Add(m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal));
        }

        return output;
    }

    [GeneratedRegex(@"^\s*""path""\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VdfPath();

    /// <summary>
    /// Reads Steam's install directory from the registry: the user's SteamPath first, then the machine-wide
    /// InstallPath. Empty when there is none (or off Windows); discovery then falls back to the environment overrides.
    /// </summary>
    private static string SteamInstallPath() => OperatingSystem.IsWindows() ? ReadSteamRegistry() : "";

    [SupportedOSPlatform("windows")]
    private static string ReadSteamRegistry()
    {
        foreach (var (root, path, name) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: false);
                if (key?.GetValue(name) is string value && value.Length > 0)
                {
                    return value.Replace('/', Path.DirectorySeparatorChar);
                }
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return "";
    }

    internal static bool IsFile(string path) => File.Exists(path);

    internal static bool IsDir(string path) => Directory.Exists(path);

    private static string ResolvedFile(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// The path absolute and cleaned, with the on-disk casing when it exists (the registry spells Steam's install
    /// path in lower case). Links are not resolved.
    /// </summary>
    internal static string AbsClean(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }

        var root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            return trimmed.Length > 0 ? trimmed : full;
        }

        var current = root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) + root[1..] : root;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var exact = true;
        foreach (var segment in segments)
        {
            string? name = null;
            if (exact)
            {
                try
                {
                    name = new DirectoryInfo(current).EnumerateFileSystemInfos(segment).FirstOrDefault()?.Name;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            exact = name is not null;
            current = Path.Combine(current, name ?? segment);
        }

        return current;
    }

    internal static string Quote(string s) => new StringBuilder("'").Append(s.Replace("'", "''", StringComparison.Ordinal)).Append('\'').ToString();
}
