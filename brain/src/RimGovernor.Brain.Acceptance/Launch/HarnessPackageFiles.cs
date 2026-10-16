using System.Security.Cryptography;

namespace RimGovernor.Brain.Acceptance;

public sealed partial class Harness
{
    /// <summary>
    /// Hashes the three files that identify a build of the unified native mod under <paramref name="installation"/> (the
    /// game folder): About.xml, Runtime.dll and Bridge.dll. This is the evidence the report records as package_files and
    /// the key of the lab stamp and checkpoint fingerprint, so it stays narrow: widening it would add start-cache misses.
    /// The kept-game relaunch check uses <see cref="RelaunchFiles"/> instead. It does not re-run the package layout
    /// checks. The package identity must be exactly the native package, and no earlier split package may sit beside it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> StampFiles(string installation)
    {
        var pkg = NativePackageDir(installation);
        return HashFiles(pkg,
        [
            Path.Combine("About", "About.xml"),
            Path.Combine("Assemblies", "RimGovernor.Runtime.dll"),
            Path.Combine("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
        ]);
    }

    /// <summary>
    /// Hashes every file a running game loaded from the native mod under <paramref name="installation"/>: About.xml and
    /// everything under Assemblies/ and BridgeTools/RimGovernor/ (the brain, host, SQLite and native libraries as well as
    /// Runtime.dll and Bridge.dll). A kept game is reused only while these match its launch snapshot, so a rebuilt brain
    /// assembly relaunches the game rather than running stale (#3293). Same identity checks as <see cref="StampFiles"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RelaunchFiles(string installation)
    {
        var pkg = NativePackageDir(installation);
        var names = new List<string> { Path.Combine("About", "About.xml") };
        foreach (var dir in new[] { "Assemblies", Path.Combine("BridgeTools", "RimGovernor") })
        {
            var full = Path.Combine(pkg, dir);
            try
            {
                if (Directory.Exists(full))
                {
                    names.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(pkg, path)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AcceptanceException($"list {dir}: {e.Message}", e);
            }
        }

        return HashFiles(pkg, names);
    }

    private static string NativePackageDir(string installation)
    {
        var pkg = Path.Combine(installation, "Mods", "RimGovernor");
        var identity = HeadlessProfile.PackageId(Path.Combine(pkg, "About", "About.xml"));
        if (!string.Equals(identity, HeadlessProfile.NativePackage, StringComparison.Ordinal))
        {
            throw new AcceptanceException("installed package metadata does not identify the unified native mod");
        }

        foreach (var old in new[] { "RimGovernorObservations", "RimGovernorHeadless" })
        {
            if (Directory.Exists(Path.Combine(installation, "Mods", old)) || File.Exists(Path.Combine(installation, "Mods", old)))
            {
                throw new AcceptanceException($"mixed package installation: {old}");
            }
        }

        return pkg;
    }

    private static Dictionary<string, string> HashFiles(string pkg, IEnumerable<string> names)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                using var stream = File.OpenRead(Path.Combine(pkg, name));
                hashes[name] = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AcceptanceException($"hash {name}: {e.Message}", e);
            }
        }

        return hashes;
    }
}
