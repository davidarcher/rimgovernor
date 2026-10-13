using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The part of native-manifest.json the setup reads: what the installed build was made from (Go <c>nativeaccept.PackageManifest</c>).</summary>
public sealed class SetupPackageManifest
{
    public string PackageId { get; init; } = "";

    public string Role { get; init; } = "";

    public IReadOnlyList<string> Fixtures { get; init; } = [];

    public string SourceRevision { get; init; } = "";

    public bool SourceDirty { get; init; }

    /// <summary>The source-tree hash over the inputs the build copied; builds from before it was recorded leave it empty.</summary>
    public string SourceTree { get; init; } = "";
}

/// <summary>
/// The installed native package as the setup sees it: its manifest, the hash of the worktree's build inputs, and the
/// layout checks. Go <c>nativeaccept</c> (<c>stale.go</c>, <c>headless.go</c> RequireNativePackage, CopyFile) and
/// <c>nativeaccept/inputs</c> (<c>SourceTreeHash</c>, <c>FindRepo</c>) own these; they are re-stated here, narrowly, so the
/// setup stands alone.
/// </summary>
public static partial class SetupPackage
{
    /// <summary>The unified native mod's package ID.</summary>
    public const string NativePackage = "davidarcher.rimgovernor.native";

    /// <summary>The build record scripts/build_native_mod.ps1 writes at the package root.</summary>
    public const string PackageManifestName = "native-manifest.json";

    /// <summary>Split/superseded native package IDs that must never coexist with the unified package.</summary>
    private static readonly HashSet<string> LegacyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "davidarcher.rimgovernor.observations",
        "brrainz.rimbridgeserver",
        "redeyedev.headlessrim",
    };

    private static readonly (string Repo, string Copy, Func<string, bool>? Keep)[] NativeSourceInputs =
    [
        ("integrations/rimgovernor-native/src", "integrations/rimgovernor-native/src", NotNativeBuildOutput),
        ("integrations/rimgovernor-native/About", "integrations/rimgovernor-native/About", NotNativeBuildOutput),
        ("integrations/rimgovernor-native/Defs", "integrations/rimgovernor-native/Defs", NotNativeBuildOutput),
        ("integrations/rimgovernor-native/Textures", "integrations/rimgovernor-native/Textures", NotNativeBuildOutput),
        ("integrations/rimgovernor-native/Notices", "integrations/rimgovernor-native/Notices", NotNativeBuildOutput),
        ("integrations/rimgovernor-native/README.md", "integrations/rimgovernor-native/README.md", null),
        ("integrations/rimgovernor-host/src", "integrations/rimgovernor-host/src", NotBuildOutput),
        ("integrations/rimgovernor-host/Directory.Build.props", "integrations/rimgovernor-host/Directory.Build.props", null),
        ("brain/Directory.Build.props", "brain/Directory.Build.props", null),
        ("brain/Directory.Packages.props", "brain/Directory.Packages.props", null),
        ("brain/src/RimGovernor.Brain.Domain", "brain/src/RimGovernor.Brain.Domain", NotBuildOutput),
        ("brain/src/RimGovernor.Brain.Contracts", "brain/src/RimGovernor.Brain.Contracts", NotBuildOutput),
        ("brain/src/RimGovernor.Brain.NativeFacade", "brain/src/RimGovernor.Brain.NativeFacade", NotBuildOutput),
        ("brain/src/RimGovernor.Brain.HttpChannel", "brain/src/RimGovernor.Brain.HttpChannel", NotBuildOutput),
        ("brain/src/RimGovernor.Brain.Telemetry", "brain/src/RimGovernor.Brain.Telemetry", NotBuildOutput),
        ("scripts/build_native_mod.ps1", "scripts/build_native_mod.ps1", null),
        ("scripts/fixtures", "scripts/fixtures", FixtureSource),
        ("THIRD_PARTY.md", "THIRD_PARTY.md", null),
    ];

    /// <summary>Reads <paramref name="pkg"/>'s native-manifest.json. Throws <see cref="IOException"/> when absent and <see cref="JsonException"/> when malformed.</summary>
    public static SetupPackageManifest ReadPackageManifest(string pkg)
    {
        var text = File.ReadAllText(Path.Combine(pkg, PackageManifestName));
        var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(PackageManifestName + ": not an object");
        var fixtures = new List<string>();
        if (root["fixtures"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    fixtures.Add(s);
                }
            }
        }

        return new SetupPackageManifest
        {
            PackageId = Str(root, "packageId"),
            Role = Str(root, "role"),
            Fixtures = fixtures,
            SourceRevision = Str(root, "sourceRevision"),
            SourceDirty = root["sourceDirty"] is JsonValue d && d.TryGetValue<bool>(out var dirty) && dirty,
            SourceTree = Str(root, "sourceTree"),
        };
    }

    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>
    /// The hash build_native_mod.ps1 records as sourceTree: over the sorted list of the build's copied source files, one
    /// <c>&lt;path&gt;\t&lt;sha256&gt;\n</c> line each with the path relative to the copy root (forward slashes), the hex
    /// SHA-256 of those lines. Computed over the worktree at <paramref name="repo"/>, it equals the manifest's when the
    /// installed build was made from these exact sources. Keep <c>NativeSourceInputs</c> in step with the build script.
    /// </summary>
    public static string SourceTreeHash(string repo)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (repoPath, copy, keep) in NativeSourceInputs)
        {
            var root = Path.Combine(repo, repoPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(root))
            {
                lines[copy] = FileHash(root);
                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new SetupException("native build input " + repoPath + ": not found");
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (keep is not null && !keep(relative))
                {
                    continue;
                }

                lines[copy + "/" + relative] = FileHash(path);
            }
        }

        var builder = new StringBuilder();
        foreach (var path in lines.Keys.Order(StringComparer.Ordinal))
        {
            _ = builder.Append(path).Append('\t').Append(lines[path]).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool NotNativeBuildOutput(string relative) => !NativeBuildDir().IsMatch(relative);

    private static bool NotBuildOutput(string relative) => !BuildOutputDir().IsMatch(relative);

    private static bool FixtureSource(string relative)
    {
        var ext = Path.GetExtension(relative).ToUpperInvariant();
        return ext is ".CS" or ".CSPROJ" && !BuildOutputDir().IsMatch(relative);
    }

    [GeneratedRegex("(^|/)(obj|bin)(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildOutputDir();

    [GeneratedRegex("(^|/)(obj|bin|Assemblies|BridgeTools)(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NativeBuildDir();

    /// <summary>Walks up from <paramref name="dir"/> to the checkout root (the directory holding .git: a directory in the main checkout, a file in a worktree); null when none encloses it.</summary>
    public static string? FindRepo(string dir)
    {
        var current = Path.GetFullPath(dir);
        while (true)
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent;
        }
    }

    /// <summary>Copies a file, creating its directory and replacing the destination (Go <c>CopyFile</c>).</summary>
    public static void CopyFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>
    /// Validates the complete unified package before preparing a private worker: the package's files must exist, its
    /// About.xml must self-identify as the native package, and no legacy split package may be installed alongside it.
    /// </summary>
    public static void RequireNativePackage(string mods)
    {
        var pkg = Path.Combine(mods, "RimGovernor");
        foreach (var relative in new[]
        {
            Path.Combine("About", "About.xml"),
            Path.Combine("Assemblies", "RimGovernor.Runtime.dll"),
            Path.Combine("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
        })
        {
            if (!File.Exists(Path.Combine(pkg, relative)))
            {
                throw new SetupException("missing unified native input: " + Path.Combine(pkg, relative));
            }
        }

        if (!string.Equals(PackageId(Path.Combine(pkg, "About", "About.xml")), NativePackage, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupException("RimGovernor package metadata does not identify the unified native mod");
        }

        var unified = 0;
        foreach (var dir in Directory.EnumerateDirectories(mods))
        {
            var metadata = Path.Combine(dir, "About", "About.xml");
            if (!File.Exists(metadata))
            {
                continue;
            }

            var other = PackageId(metadata);
            if (string.Equals(other, NativePackage, StringComparison.OrdinalIgnoreCase))
            {
                unified++;
            }

            if (LegacyPackages.Contains(other))
            {
                throw new SetupException("remove split native packages from fresh worker inputs: " + dir);
            }
        }

        if (unified != 1)
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"fresh worker inputs require exactly one unified native package, found {unified}"));
        }
    }

    private static string PackageId(string aboutXmlPath)
    {
        try
        {
            return XDocument.Load(aboutXmlPath).Root?.Element("packageId")?.Value.Trim() ?? "";
        }
        catch (System.Xml.XmlException e)
        {
            throw new SetupException($"parse {aboutXmlPath}: {e.Message}", e);
        }
    }
}
