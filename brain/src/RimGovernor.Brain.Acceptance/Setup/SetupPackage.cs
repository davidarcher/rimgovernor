using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// native-manifest.json, the build record <see cref="NativeModBuild"/> writes at the package root: what the installed build
/// was made from (Go <c>nativeaccept.PackageManifest</c>).
/// </summary>
public sealed class SetupPackageManifest
{
    public int FormatVersion { get; init; }

    public string PackageId { get; init; } = "";

    public string Role { get; init; } = "";

    public IReadOnlyList<string> Fixtures { get; init; } = [];

    public IReadOnlyList<ManifestDependency> RuntimeDependencies { get; init; } = [];

    public IReadOnlyList<ManifestDependency> HostDependencies { get; init; } = [];

    public string SourceRevision { get; init; } = "";

    public bool SourceDirty { get; init; }

    /// <summary>The source-tree hash over the inputs the build copied; builds from before it was recorded leave it empty.</summary>
    public string SourceTree { get; init; } = "";

    public string DotnetSdk { get; init; } = "";

    /// <summary>The game files the build compiled against (full paths) and their hashes.</summary>
    public IReadOnlyList<ManifestFile> Inputs { get; init; } = [];

    /// <summary>Every file of the package (paths relative to it, forward slashes) and its hash.</summary>
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];
}

/// <summary>
/// The installed native package as the setup sees it: its manifest, the hash of the worktree's build inputs, and the
/// layout checks. Go <c>nativeaccept</c> (<c>stale.go</c>, <c>headless.go</c> RequireNativePackage, CopyFile) and
/// <c>nativeaccept/inputs</c> (<c>SourceTreeHash</c>, <c>FindRepo</c>) own these; they are re-stated here, narrowly, so the
/// setup stands alone.
/// </summary>
public static class SetupPackage
{
    /// <summary>The unified native mod's package ID.</summary>
    public const string NativePackage = "davidarcher.rimgovernor.native";

    /// <summary>The build record <see cref="NativeModBuild"/> writes at the package root.</summary>
    public const string PackageManifestName = "native-manifest.json";

    /// <summary>Split/superseded native package IDs that must never coexist with the unified package.</summary>
    private static readonly HashSet<string> LegacyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "davidarcher.rimgovernor.observations",
        "brrainz.rimbridgeserver",
        "redeyedev.headlessrim",
    };

    /// <summary>Reads <paramref name="pkg"/>'s native-manifest.json. Throws <see cref="IOException"/> when absent and <see cref="JsonException"/> when malformed.</summary>
    public static SetupPackageManifest ReadPackageManifest(string pkg) =>
        LaunchJson.ParseManifest(File.ReadAllText(Path.Combine(pkg, PackageManifestName)));

    /// <summary>The sourceTree hash of the build inputs under <paramref name="repo"/> (<see cref="NativeSourceInputs.SourceTreeHash(string)"/>), a missing input reported as a <see cref="SetupException"/>.</summary>
    public static string SourceTreeHash(string repo)
    {
        try
        {
            return NativeSourceInputs.SourceTreeHash(repo);
        }
        catch (AcceptanceException e) when (e is not SetupException)
        {
            throw new SetupException(e.Message, e);
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

        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A process that has the destination mapped ("a file with a user-mapped section open") blocks the overwrite but
            // usually not a rename, so move the old file aside and write the new one in its place. The aside file stays: it
            // is still mapped, it is harmless, and its name records which file was locked.
            File.Move(destination, destination + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
            File.Copy(source, destination, overwrite: false);
        }
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
