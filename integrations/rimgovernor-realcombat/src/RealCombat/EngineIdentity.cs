using System.Security.Cryptography;
using System.Text;
using Verse;

namespace RimGovernor.RealCombat;

/// <summary>Content identity of the hypothetical runtime, not just same-named installed mods.</summary>
public static class EngineIdentity
{
    public static string GameBuild => FileHash(typeof(Pawn).Assembly.Location);

    public static string Mods()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var mod in LoadedModManager.RunningModsListForReading.OrderBy(m => m.PackageId, StringComparer.Ordinal))
        {
            Add(hash, mod.PackageId);
            foreach (var file in Directory.EnumerateFiles(mod.RootDir, "*", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                Add(hash, Path.GetRelativePath(mod.RootDir, file).Replace('\\', '/'));
                Add(hash, FileHash(file));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Runtime(string game)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(game, "*.dll").Order(StringComparer.Ordinal))
        {
            Add(hash, Path.GetFileName(file)); Add(hash, FileHash(file));
        }
        Add(hash, FileHash(typeof(EngineIdentity).Assembly.Location));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Add(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
}
