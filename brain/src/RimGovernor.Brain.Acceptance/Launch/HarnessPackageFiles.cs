using System.Security.Cryptography;

namespace RimGovernor.Brain.Acceptance;

public sealed partial class Harness
{
    /// <summary>
    /// Hashes the unified native mod's on-disk files under <paramref name="installation"/> (the game folder; the evidence
    /// the report records as package_files and the launch snapshot compares). It does not re-run the package layout
    /// checks. The package identity must be exactly the native package, and no earlier split package may sit beside it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PackageFiles(string installation)
    {
        var pkg = Path.Combine(installation, "Mods", "RimGovernor");
        string[] files =
        [
            Path.Combine("About", "About.xml"),
            Path.Combine("Assemblies", "RimGovernor.Runtime.dll"),
            Path.Combine("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
        ];
        var identity = HeadlessProfile.PackageId(Path.Combine(pkg, "About", "About.xml"));
        if (!string.Equals(identity, HeadlessProfile.NativePackage, StringComparison.Ordinal))
        {
            throw new AcceptanceException("installed package metadata does not identify the unified native mod");
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in files)
        {
            try
            {
                hashes[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(pkg, name))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AcceptanceException($"hash {name}: {e.Message}", e);
            }
        }

        foreach (var old in new[] { "RimGovernorObservations", "RimGovernorHeadless" })
        {
            if (Directory.Exists(Path.Combine(installation, "Mods", old)) || File.Exists(Path.Combine(installation, "Mods", old)))
            {
                throw new AcceptanceException($"mixed package installation: {old}");
            }
        }

        return hashes;
    }
}
