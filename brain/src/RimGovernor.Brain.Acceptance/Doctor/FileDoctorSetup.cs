using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The self-contained <see cref="IDoctorSetup"/>: reads the root's <c>config/config.json</c>, the package manifest, the
/// ModsConfig.xml and the installed expansions straight from disk. The stale-package comparison, the fixture catalogue
/// and the launched-state comparison need the Setup/Lifecycle code; here the stale check reports itself skipped, the
/// fixture list is the installed one and a launch is never reported as mismatched. The Setup layer supplies a fuller
/// implementation through <see cref="AcceptanceDoctor.Setup"/>.
/// </summary>
public sealed class FileDoctorSetup : IDoctorSetup
{
    public string GameId => "rimgovernor-trial";

    public int MaxGameCopyPath => 140;

    public int StoreSchemaVersion { get; init; } = 3;

    public string BaselineSave => "RimGovernor-tribal8-baseline.rws";

    public string BaselineName => "RimGovernor-tribal8-baseline";

    public string AllowStaleModEnv => "RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD";

    public string PackageManifestName => "native-manifest.json";

    public string? FindRepo(string dir)
    {
        for (DirectoryInfo? d = new(dir); d is not null; d = d.Parent)
        {
            string git = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return d.FullName;
            }
        }

        return null;
    }

    public DoctorGameSection ReadGameSection(string root, string gameId)
    {
        string path = Path.Combine(root, "config", "config.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject config)
            {
                throw new AcceptanceException("configuration is not a JSON object");
            }

            if (config["games"] is not JsonObject games)
            {
                throw new AcceptanceException("configuration is missing games");
            }

            if (games[gameId] is not JsonObject game)
            {
                throw new AcceptanceException("configuration is missing games." + gameId);
            }

            return new DoctorGameSection((string?)game["workingDir"] ?? "", (string?)game["target"] ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new AcceptanceException("read " + path + ": " + e.Message, e);
        }
    }

    public void RequireNativePackage(string mods)
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

    public DoctorPackageManifest? ReadPackageManifest(string package)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(package, PackageManifestName))) is not JsonObject m)
            {
                return null;
            }

            IReadOnlyList<string> fixtures = m["fixtures"] is JsonArray a ? [.. a.Select(n => (string?)n ?? "").Where(s => s.Length > 0)] : [];
            return new DoctorPackageManifest((string?)m["role"] ?? "", fixtures, (string?)m["sourceRevision"] ?? "", m["sourceDirty"] is JsonValue v && v.TryGetValue<bool>(out bool dirty) && dirty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public string RequireCurrentPackage(string package, IReadOnlyList<string> fixtureOps) => "stale-package comparison is not available in the standalone doctor";

    public IReadOnlyList<string> FixtureFlags(string repo, IReadOnlyList<string> installed, IReadOnlyList<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return [.. installed.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> InstalledExpansions(string gameCopy)
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

    public IReadOnlyList<string> ActiveMods(string modsConfigPath)
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

    public string LaunchedMismatch(string headlessConfigDir) => "";

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
