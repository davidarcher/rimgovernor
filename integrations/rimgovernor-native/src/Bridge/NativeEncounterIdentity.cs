#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The content identity the prediction worker is checked against: the game assembly's hash, every running mod's
/// content hash, and the official mods' content hash the worker boots with. The algorithm is RealCombat's
/// <c>EngineIdentity</c> (package id, then each xml/dll path and file hash, ordered), so a request observed here is
/// accepted by a worker built from the same game files and refused when either side differs.
/// </summary>
internal static class NativeEncounterIdentity
{
    internal sealed record Identity(string GameBuild, string Mods, string PredictorMods, string GameDirectory);

    internal sealed record ModRoot(string PackageId, string RootDir);

    private static volatile Identity? current;

    /// <summary>The identity once <see cref="ComputeAsync"/> has finished; null until then, and a hunt scene is unobserved while it is.</summary>
    internal static Identity? Current => current;

    /// <summary>The running mods' roots; read on the game thread because the mod list is the game's.</summary>
    internal static IReadOnlyList<ModRoot> Snapshot() =>
        LoadedModManager.RunningModsListForReading.Select(m => new ModRoot(m.PackageId, m.RootDir)).ToArray();

    /// <summary>Hashes the game assembly and the mod files off the game thread, publishes the result as <see cref="Current"/>.</summary>
    internal static Task<Identity> ComputeAsync(IReadOnlyList<ModRoot> mods, CancellationToken cancellation) => Task.Run(() =>
    {
        var assembly = typeof(Pawn).Assembly.Location;
        var identity = new Identity(FileHash(assembly), ModsHash(mods, false, cancellation), ModsHash(mods, true, cancellation), Path.GetDirectoryName(assembly)!);
        current = identity;
        return identity;
    }, cancellation);

    private static string ModsHash(IReadOnlyList<ModRoot> mods, bool officialOnly, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var mod in mods.Where(m => !officialOnly || m.PackageId.StartsWith("ludeon.rimworld", StringComparison.Ordinal)).OrderBy(m => m.PackageId, StringComparer.Ordinal))
        {
            Add(hash, mod.PackageId);
            foreach (var file in Directory.EnumerateFiles(mod.RootDir, "*", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                Add(hash, Path.GetRelativePath(mod.RootDir, file).Replace('\\', '/'));
                Add(hash, FileHash(file));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Add(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
}
