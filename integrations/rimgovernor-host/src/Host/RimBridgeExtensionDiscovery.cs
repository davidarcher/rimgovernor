using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RimGovernor.Host.Core;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Host;

internal static class RimBridgeExtensionDiscovery
{
    private sealed class LoadedCompanionAssembly
    {
        public Assembly Assembly { get; set; }

        public CompanionAssemblyCandidate Candidate { get; set; }
    }

    private sealed class DiscoveredMethodCandidate
    {
        public CompanionAssemblyCandidate Candidate { get; set; }

        public Type Type { get; set; }

        public MethodInfo Method { get; set; }
    }

    private static readonly object ResolverSync = new();
    private static readonly HashSet<string> BundleDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Assembly> LoadedAssembliesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static bool _resolverInstalled;

    /// <summary>Loads the tool assemblies bundled in this mod's BridgeTools folder and builds a provider per assembly.</summary>
    public static IReadOnlyList<AnnotatedExtensionCapabilityProvider> DiscoverProviders()
    {
        InstallResolver();
        var loadedAssemblies = LoadCompanionAssemblies();
        var candidates = new List<DiscoveredMethodCandidate>();

        foreach (var loaded in loadedAssemblies)
        {
            try
            {
                CollectToolCandidates(loaded, candidates);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] Failed to scan companion assembly '{loaded.Candidate.AssemblyPath}': {ex}");
            }
        }

        return BuildProviders(candidates);
    }

    private static IReadOnlyList<LoadedCompanionAssembly> LoadCompanionAssemblies()
    {
        var result = new List<LoadedCompanionAssembly>();
        foreach (var candidate in DiscoverCompanionCandidates())
        {
            try
            {
                RegisterBundleDirectories(candidate);
                var assembly = LoadBundledAssembly(candidate.AssemblyPath);
                if (assembly != null)
                {
                    result.Add(new LoadedCompanionAssembly
                    {
                        Assembly = assembly,
                        Candidate = candidate
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] Failed to load companion assembly '{candidate.AssemblyPath}': {ex}");
            }
        }

        return result;
    }

    /// <summary>This mod's own BridgeTools folders; the host loads no other mod's tools.</summary>
    private static IEnumerable<CompanionAssemblyCandidate> DiscoverCompanionCandidates()
    {
        var mod = LoadedModManager.GetMod<RimGovernorHostMod>()?.Content;
        if (mod == null)
        {
            Log.Error("[RimBridge] The RimGovernor mod content pack is not loaded; no tools will be registered.");
            yield break;
        }

        var folders = mod.foldersToLoadDescendingOrder?
            .Where(folder => string.IsNullOrWhiteSpace(folder) == false)
            .ToList()
            ?? [];
        var discovered = new List<(string RelativeKey, CompanionAssemblyCandidate Candidate)>();

        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var root = Path.Combine(folders[i], CompanionFileDiscovery.BridgeToolsFolderName);
            foreach (var candidate in CompanionFileDiscovery.DiscoverBridgeToolsRoot(root, CreateOwnerId(mod)))
                discovered.Add((CreateRelativeKey(candidate), candidate));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = discovered.Count - 1; i >= 0; i--)
        {
            if (seen.Add(discovered[i].RelativeKey) == false)
                discovered.RemoveAt(i);
        }

        foreach (var entry in discovered)
            yield return entry.Candidate;
    }

    private static string CreateRelativeKey(CompanionAssemblyCandidate candidate)
    {
        var root = candidate.BridgeToolsRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        var path = candidate.AssemblyPath ?? string.Empty;
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.GetFileName(path);
    }

    private static void CollectToolCandidates(LoadedCompanionAssembly loaded, IList<DiscoveredMethodCandidate> discovered)
    {
        foreach (var type in SafeGetTypes(loaded.Assembly, loaded.Candidate).OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal))
        {
            try
            {
                if (type == null || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                var annotatedMethods = GetAnnotatedMethods(type);
                if (annotatedMethods.Count == 0)
                    continue;

                foreach (var method in annotatedMethods)
                {
                    discovered.Add(new DiscoveredMethodCandidate
                    {
                        Candidate = loaded.Candidate,
                        Type = type,
                        Method = method
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] Failed to inspect companion tool type '{type?.FullName ?? type?.Name ?? "unknown-type"}' from '{loaded.Candidate.AssemblyPath}': {ex}");
            }
        }

    }

    private static IReadOnlyList<AnnotatedExtensionCapabilityProvider> BuildProviders(IEnumerable<DiscoveredMethodCandidate> candidates)
    {
        var providers = new List<AnnotatedExtensionCapabilityProvider>();

        foreach (var assemblyGroup in candidates
            .GroupBy(candidate => candidate.Candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var providerCandidates = assemblyGroup.ToList();
            var toolClasses = new List<AnnotatedExtensionCapabilityProvider.ToolClass>();

            foreach (var typeGroup in providerCandidates
                .GroupBy(candidate => candidate.Type)
                .OrderBy(group => group.Key.FullName ?? group.Key.Name, StringComparer.Ordinal))
            {
                try
                {
                    var methods = typeGroup
                        .Select(candidate => candidate.Method)
                        .OrderBy(method => method.Name, StringComparer.Ordinal)
                        .ThenBy(GetMetadataToken)
                        .ToList();

                    if (!TryCreateInstance(typeGroup.Key, methods, out var instance, out var error))
                    {
                        Log.Warning($"[RimBridge] Skipping companion tool type '{typeGroup.Key.FullName ?? typeGroup.Key.Name}' from '{assemblyGroup.Key}': {error}");
                        continue;
                    }

                    toolClasses.Add(new AnnotatedExtensionCapabilityProvider.ToolClass
                    {
                        Type = typeGroup.Key,
                        Instance = instance,
                        Methods = methods
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimBridge] Failed to prepare companion tool type '{typeGroup.Key?.FullName ?? typeGroup.Key?.Name ?? "unknown-type"}': {ex}");
                }
            }

            if (toolClasses.Count == 0)
                continue;

            var representative = providerCandidates[0].Candidate;
            var providerId = CreateProviderId(representative);
            providers.Add(new AnnotatedExtensionCapabilityProvider(
                providerId: providerId,
                category: "extension",
                toolClasses: toolClasses));
        }

        return providers;
    }

    private static bool TryCreateInstance(
        Type type,
        IReadOnlyCollection<MethodInfo> annotatedMethods,
        out object instance,
        out string error)
    {
        instance = null;
        error = string.Empty;

        if (annotatedMethods.All(method => method.IsStatic))
            return true;

        var constructor = type.GetConstructor(Type.EmptyTypes);
        if (constructor == null)
        {
            error = $"type '{type.FullName ?? type.Name}' has instance tool methods but no public parameterless constructor.";
            return false;
        }

        try
        {
            instance = Activator.CreateInstance(type);
            return true;
        }
        catch (Exception ex)
        {
            error = $"creating '{type.FullName ?? type.Name}' failed: {ex.GetBaseException()}";
            return false;
        }
    }

    private static List<MethodInfo> GetAnnotatedMethods(Type type)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.IsSpecialName == false)
            .Where(method => method.ContainsGenericParameters == false)
            .Where(method => method.GetCustomAttribute<ToolAttribute>(inherit: false) != null)
            .ToList();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly, CompanionAssemblyCandidate candidate)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            if (ex.LoaderExceptions != null)
            {
                foreach (var loaderException in ex.LoaderExceptions.Where(exception => exception != null))
                    Log.Warning($"[RimBridge] Loader exception while scanning companion assembly '{candidate.AssemblyPath}': {loaderException.Message}");
            }

            return ex.Types.Where(type => type != null);
        }
    }

    private static void InstallResolver()
    {
        lock (ResolverSync)
        {
            if (_resolverInstalled)
                return;

            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledAssembly;
            _resolverInstalled = true;
        }
    }

    /// <summary>
    /// Binds a bundled assembly's references by simple name: an assembly the game
    /// already loaded (the host's SDK among them) wins, otherwise the DLL beside
    /// the tools. Assembly.LoadFile registers no binding context of its own.
    /// </summary>
    private static Assembly ResolveBundledAssembly(object sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name).Name;
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
            {
                try
                {
                    return assembly.IsDynamic == false
                        && string.Equals(assembly.GetName().Name, requestedName, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        if (loaded != null)
            return loaded;

        string[] directories;
        lock (ResolverSync)
            directories = BundleDirectories.ToArray();

        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, requestedName + ".dll");
            if (File.Exists(path))
                return LoadBundledAssembly(path);
        }

        return null;
    }

    private static Assembly LoadBundledAssembly(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (ResolverSync)
        {
            if (LoadedAssembliesByPath.TryGetValue(fullPath, out var existing))
                return existing;
        }

        var assembly = Assembly.LoadFile(fullPath);
        lock (ResolverSync)
            LoadedAssembliesByPath[fullPath] = assembly;

        return assembly;
    }

    private static void RegisterBundleDirectories(CompanionAssemblyCandidate candidate)
    {
        lock (ResolverSync)
        {
            if (string.IsNullOrWhiteSpace(candidate.BundleDirectory) == false)
                BundleDirectories.Add(candidate.BundleDirectory);
            if (string.IsNullOrWhiteSpace(candidate.BridgeToolsRoot) == false)
                BundleDirectories.Add(candidate.BridgeToolsRoot);
        }
    }

    private static string CreateProviderId(CompanionAssemblyCandidate candidate)
    {
        var owner = ReflectedCapabilityBinding.ToKebabCase(candidate.OwnerId);
        if (string.IsNullOrWhiteSpace(owner))
            owner = "mod";

        var assembly = ReflectedCapabilityBinding.ToKebabCase(Path.GetFileNameWithoutExtension(candidate.AssemblyPath));
        if (string.IsNullOrWhiteSpace(assembly))
            assembly = "companion";

        return $"extension.mod/{owner}/{assembly}";
    }

    private static string CreateOwnerId(ModContentPack mod)
    {
        return mod?.PackageId ?? mod?.Name ?? mod?.FolderName ?? "unknown-mod";
    }

    private static int GetMetadataToken(MethodInfo method)
    {
        try
        {
            return method.MetadataToken;
        }
        catch
        {
            return 0;
        }
    }
}
