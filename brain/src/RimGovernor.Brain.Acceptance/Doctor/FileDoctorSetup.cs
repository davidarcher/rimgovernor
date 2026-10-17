using System.Text.Json;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What the doctor needs from the setup and launch layers: reads the root's <c>config/config.json</c>, the package manifest, the ModsConfig.xml and the installed
/// expansions straight from disk. The stale-package comparison, the fixture catalogue and the launched-state comparison
/// need the Setup/Lifecycle code; here the stale check reports itself skipped, the fixture list is the installed one and
/// a launch is never reported as mismatched.
/// </summary>
public static class FileDoctorSetup
{
    public static string GameId => "rimgovernor-trial";

    public static int MaxGameCopyPath => 140;

    public const int StoreSchemaVersion = 3;

    public static string BaselineSave => "RimGovernor-tribal8-baseline.rws";

    public static string BaselineName => "RimGovernor-tribal8-baseline";

    public static string AllowStaleModEnv => "RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD";

    public static string PackageManifestName => "native-manifest.json";

    public static string? FindRepo(string dir) => NativeSourceInputs.FindRepo(dir);

    public static DoctorGameSection ReadGameSection(string root, string gameId)
    {
        string path = Path.Combine(root, "config", "config.json");
        try
        {
            var config = LaunchJson.ParseConfig(File.ReadAllBytes(path));
            if (config.Games is not { } games)
            {
                throw new AcceptanceException("configuration is missing games");
            }

            if (games.GetValueOrDefault(gameId) is not { } game)
            {
                throw new AcceptanceException("configuration is missing games." + gameId);
            }

            return new DoctorGameSection(game.WorkingDir ?? "", game.Target ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AcceptanceException("read " + path + ": " + e.Message, e);
        }
    }

    public static void RequireNativePackage(string mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        string pkg = Path.Combine(mods, "RimGovernor");
        foreach (string relative in new[] { Path.Combine("About", "About.xml"), Path.Combine("Assemblies", "RimGovernor.Runtime.dll") })
        {
            if (!File.Exists(Path.Combine(pkg, relative)))
            {
                throw new AcceptanceException("missing unified native input: " + Path.Combine(pkg, relative));
            }
        }
    }

    public static DoctorPackageManifest? ReadPackageManifest(string package)
    {
        try
        {
            var m = LaunchJson.ParseManifest(File.ReadAllText(Path.Combine(package, PackageManifestName)));
            IReadOnlyList<string> fixtures = [.. m.Fixtures.Where(f => !string.IsNullOrEmpty(f))];
            return new DoctorPackageManifest(m.Role ?? "", fixtures, m.SourceRevision ?? "", m.SourceDirty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string RequireCurrentPackage(string package, IReadOnlyList<string> fixtureOps) => "stale-package comparison is not available in the standalone doctor";

    public static IReadOnlyList<string> FixtureFlags(string repo, IReadOnlyList<string> installed, IReadOnlyList<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return [.. installed.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string> InstalledExpansions(string gameCopy)
    {
        string data = Path.Combine(gameCopy, "Data");
        var ids = new List<string>();
        if (Directory.Exists(data))
        {
            foreach (string dir in Directory.EnumerateDirectories(data))
            {
                string about = Path.Combine(dir, "About", "About.xml");
                if (!File.Exists(about))
                {
                    continue;
                }

                string id = PackageId(about).ToLowerInvariant();
                if (id.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal))
                {
                    ids.Add(id);
                }
            }
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    public static IReadOnlyList<string> ActiveMods(string modsConfigPath)
    {
        XElement root;
        try
        {
            root = XDocument.Load(modsConfigPath).Root ?? throw new AcceptanceException(modsConfigPath + ": empty document");
        }
        catch (System.Xml.XmlException e)
        {
            throw new AcceptanceException("parse " + modsConfigPath + ": " + e.Message, e);
        }

        XElement active = root.Element("activeMods") ?? throw new AcceptanceException(modsConfigPath + ": missing activeMods");
        return [.. active.Elements("li").Select(li => li.Value.Trim().ToLowerInvariant())];
    }

    public static string LaunchedMismatch(string headlessConfigDir) => "";

    private static string PackageId(string aboutXml)
    {
        try
        {
            return XDocument.Load(aboutXml).Root?.Element("packageId")?.Value.Trim() ?? "";
        }
        catch (System.Xml.XmlException e)
        {
            throw new AcceptanceException("parse " + aboutXml + ": " + e.Message, e);
        }
    }
}
