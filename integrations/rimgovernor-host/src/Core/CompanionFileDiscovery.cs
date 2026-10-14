using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimGovernor.Host.Core;

public sealed class CompanionAssemblyCandidate
{
    public string AssemblyPath { get; set; } = string.Empty;

    public string BridgeToolsRoot { get; set; } = string.Empty;

    public string BundleDirectory { get; set; }

    public string OwnerId { get; set; } = string.Empty;
}

public static class CompanionFileDiscovery
{
    public const string BridgeToolsFolderName = "BridgeTools";
    public const string SdkAssemblyName = "RimGovernor.Host.Sdk";
    public const string SdkAssemblyFileName = SdkAssemblyName + ".dll";

    public static IReadOnlyList<CompanionAssemblyCandidate> DiscoverBridgeToolsRoot(
        string bridgeToolsRoot,
        string ownerId)
    {
        if (string.IsNullOrWhiteSpace(bridgeToolsRoot) || Directory.Exists(bridgeToolsRoot) == false)
            return [];

        var root = Path.GetFullPath(bridgeToolsRoot);
        var candidates = new List<CompanionAssemblyCandidate>();

        foreach (var file in Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(IsNotSdkAssembly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(new CompanionAssemblyCandidate
            {
                AssemblyPath = Path.GetFullPath(file),
                BridgeToolsRoot = root,
                OwnerId = ownerId ?? string.Empty
            });
        }

        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            candidates.AddRange(DiscoverBundleDirectory(root, directory, ownerId));
        }

        return candidates;
    }

    private static IEnumerable<CompanionAssemblyCandidate> DiscoverBundleDirectory(
        string root,
        string bundleDirectory,
        string ownerId)
    {
        var bundleName = Path.GetFileName(bundleDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var preferredEntry = Path.Combine(bundleDirectory, bundleName + ".dll");
        var entries = File.Exists(preferredEntry)
            ? [preferredEntry]
            : Directory.GetFiles(bundleDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(IsNotSdkAssembly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var entry in entries)
        {
            if (IsNotSdkAssembly(entry) == false)
                continue;

            yield return new CompanionAssemblyCandidate
            {
                AssemblyPath = Path.GetFullPath(entry),
                BridgeToolsRoot = root,
                BundleDirectory = Path.GetFullPath(bundleDirectory),
                OwnerId = ownerId ?? string.Empty
            };
        }
    }

    // A bundle folder also carries native libraries (e_sqlite3.dll); loading one as a managed assembly only logs a
    // BadImageFormatException, so a dll with no managed metadata is not a companion candidate.
    private static bool IsNotSdkAssembly(string path)
    {
        return string.Equals(Path.GetFileName(path), SdkAssemblyFileName, StringComparison.OrdinalIgnoreCase) == false
            && IsManagedAssembly(path);
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (Exception)
        {
            // Unreadable for another reason: leave it to the loader, which reports the real error.
            return true;
        }
    }
}
