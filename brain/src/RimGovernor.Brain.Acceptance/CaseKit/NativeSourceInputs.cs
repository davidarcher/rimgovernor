using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Knows which checked-in files an acceptance harness run and the native mod build depend on, and hashes them. It has no game or transport dependency so the tools that only need the hashes (the
/// verified, land and affected commands) stay cheap.
/// </summary>
public static partial class NativeSourceInputs
{
    /// <summary>
    /// One rule of the source copy <see cref="NativeModBuild"/> makes under build/source: which repository path it takes,
    /// where it lands relative to the copy and which files it keeps (a relative path under the repo with forward
    /// slashes; null keeps everything).
    /// </summary>
    private sealed record Input(string Repo, string Copy, Func<string, bool>? Keep = null);

    [GeneratedRegex("(^|/)(obj|bin)(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildOutputDir();

    [GeneratedRegex("(^|/)(obj|bin|Assemblies|BridgeTools)(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NativeBuildDir();

    private static bool NotNativeBuildOutput(string relative) => !NativeBuildDir().IsMatch(relative);

    private static bool NotBuildOutput(string relative) => !BuildOutputDir().IsMatch(relative);

    private static bool FixtureSource(string relative)
    {
        var ext = Path.GetExtension(relative).ToLowerInvariant();
        return (ext is ".cs" or ".csproj") && !BuildOutputDir().IsMatch(relative);
    }

    /// <summary>What the native build compiles: every file under these, copied to the same relative path.</summary>
    private static readonly Input[] Inputs =
    [
        new("integrations/rimgovernor-native/src", "integrations/rimgovernor-native/src", NotNativeBuildOutput),
        new("integrations/rimgovernor-native/About", "integrations/rimgovernor-native/About", NotNativeBuildOutput),
        new("integrations/rimgovernor-native/Defs", "integrations/rimgovernor-native/Defs", NotNativeBuildOutput),
        new("integrations/rimgovernor-native/Textures", "integrations/rimgovernor-native/Textures", NotNativeBuildOutput),
        new("integrations/rimgovernor-native/Notices", "integrations/rimgovernor-native/Notices", NotNativeBuildOutput),
        new("integrations/rimgovernor-native/README.md", "integrations/rimgovernor-native/README.md"),
        new("integrations/rimgovernor-host/src", "integrations/rimgovernor-host/src", NotBuildOutput),
        new("integrations/rimgovernor-host/Directory.Build.props", "integrations/rimgovernor-host/Directory.Build.props"),
        new("brain/Directory.Build.props", "brain/Directory.Build.props"),
        new("brain/Directory.Packages.props", "brain/Directory.Packages.props"),
        new("brain/Game.props", "brain/Game.props"),
        new("brain/src/RimGovernor.Brain.Domain", "brain/src/RimGovernor.Brain.Domain", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Policy", "brain/src/RimGovernor.Brain.Policy", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Contracts", "brain/src/RimGovernor.Brain.Contracts", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Bridge", "brain/src/RimGovernor.Brain.Bridge", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Bridge.Contracts", "brain/src/RimGovernor.Brain.Bridge.Contracts", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.HttpChannel", "brain/src/RimGovernor.Brain.HttpChannel", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Telemetry", "brain/src/RimGovernor.Brain.Telemetry", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Host", "brain/src/RimGovernor.Brain.Host", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Executor", "brain/src/RimGovernor.Brain.Executor", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Observation", "brain/src/RimGovernor.Brain.Observation", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Store", "brain/src/RimGovernor.Brain.Store", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.BuildingRuntime", "brain/src/RimGovernor.Brain.BuildingRuntime", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.PlayerApi", "brain/src/RimGovernor.Brain.PlayerApi", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Simulation", "brain/src/RimGovernor.Brain.Simulation", NotBuildOutput),
        // The stage spec records fixture builds compile in from source (#3393).
        new("brain/src/RimGovernor.Fixtures.Contracts", "brain/src/RimGovernor.Fixtures.Contracts", NotBuildOutput),
        new("scripts/fixtures", "scripts/fixtures", FixtureSource),
        new("THIRD_PARTY.md", "THIRD_PARTY.md"),
    ];

    /// <summary>The paths the native build copies from, repo-relative with forward slashes (the same rules <see cref="SourceTreeHash(string)"/> walks).</summary>
    public static IReadOnlyList<string> NativeSourceRoots() => [.. Inputs.Select(input => input.Repo)];

    /// <summary>
    /// The hash the native build records as sourceTree: over the sorted list of the build's copied source files,
    /// one <c>"&lt;path&gt;\t&lt;sha256&gt;\n"</c> line each with the path relative to the copy root (forward
    /// slashes), it is the hex SHA-256 of those lines. Computed over the worktree at <paramref name="repo"/>, it
    /// equals the manifest's when the installed build was made from these exact sources.
    /// <para>
    /// Reading every input costs up to a second, so a fingerprint of the input set -- each file's copy path, length
    /// and last-write time -- stands in front of the content hash (the trust model of rg's <c>SelfBuild</c>, #3296).
    /// When it matches the last computation's, that computation's hash is returned without reading any file: from this
    /// process's memory, else from <see cref="StampFile"/> under the worktree's <c>.rimgovernor/</c>. An edit that keeps
    /// both a file's length and its last-write time is therefore not seen; any change of size or time, or an added or
    /// removed file, recomputes.
    /// </para>
    /// </summary>
    public static string SourceTreeHash(string repo) => SourceTreeHash(repo, File.ReadAllBytes);

    /// <summary>The worktree-relative file that carries the last fingerprint and hash across processes.</summary>
    public const string StampFile = ".rimgovernor/source-tree.stamp";

    private const string StampVersion = "source-tree-stamp v1";

    private static readonly Lock CacheLock = new();

    private static readonly Dictionary<string, (string Fingerprint, string Hash)> Cached = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="SourceTreeHash(string)"/> with the file reader injected, so tests can count content reads.</summary>
    internal static string SourceTreeHash(string repo, Func<string, byte[]> read)
    {
        var key = Path.GetFullPath(repo);
        // The fingerprint is taken before any content is read: a file changed mid-hash then fails the next match
        // instead of pairing a new fingerprint with old content.
        var files = InputFiles(repo);
        var fingerprint = Fingerprint(files);
        lock (CacheLock)
        {
            if (Cached.TryGetValue(key, out var hit) && hit.Fingerprint == fingerprint)
            {
                return hit.Hash;
            }
        }

        var stamp = Path.Combine(repo, StampFile.Replace('/', Path.DirectorySeparatorChar));
        var hash = ReadStamp(stamp, fingerprint);
        if (hash is null)
        {
            var lines = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (copy, file) in files)
            {
                lines[copy] = Convert.ToHexStringLower(SHA256.HashData(read(file.FullName)));
            }

            hash = HashLines(lines);
            _ = WriteStamp(stamp, fingerprint, hash);
        }

        lock (CacheLock)
        {
            Cached[key] = (fingerprint, hash);
        }

        return hash;
    }

    /// <summary>Drops this process's remembered hashes (tests of the cross-process stamp).</summary>
    internal static void ForgetCachedHashes()
    {
        lock (CacheLock)
        {
            Cached.Clear();
        }
    }

    /// <summary>
    /// Copies the build's inputs from <paramref name="repo"/> to the same relative paths under <paramref name="to"/>
    /// (keeping timestamps) and returns each copy's sha256, keyed by that path: <see cref="HashLines"/> of it is the
    /// copy's sourceTree.
    /// </summary>
    internal static SortedDictionary<string, string> CopySources(string repo, string to)
    {
        var files = InputFiles(repo).ToList();
        var hashes = new string[files.Count];
        _ = Parallel.For(0, files.Count, i =>
        {
            var (copy, file) = files[i];
            var target = Path.Combine(to, copy.Replace('/', Path.DirectorySeparatorChar));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = File.ReadAllBytes(file.FullName);
            File.WriteAllBytes(target, bytes);
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            hashes[i] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        });
        var copied = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            copied[files[i].Key] = hashes[i];
        }

        return copied;
    }

    /// <summary>The build's copied source files under <paramref name="repo"/>, keyed by their path relative to the copy root.</summary>
    private static SortedDictionary<string, FileInfo> InputFiles(string repo)
    {
        var files = new SortedDictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var input in Inputs)
        {
            var root = Path.Combine(repo, input.Repo.Replace('/', Path.DirectorySeparatorChar));
            var single = new FileInfo(root);
            if (single.Exists)
            {
                files[input.Copy] = single;
                continue;
            }

            var dir = new DirectoryInfo(root);
            if (!dir.Exists)
            {
                throw new AcceptanceException($"native build input {input.Repo}: not found");
            }

            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (input.Keep is not null && !input.Keep(relative))
                {
                    continue;
                }

                files[input.Copy + "/" + relative] = file;
            }
        }

        return files;
    }

    private static string Fingerprint(SortedDictionary<string, FileInfo> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (copy, file) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{copy}\t{file.Length}\t{file.LastWriteTimeUtc.Ticks}\n")));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The stamp's hash when it records <paramref name="fingerprint"/>; null when absent, unreadable or for other inputs.</summary>
    private static string? ReadStamp(string stamp, string fingerprint)
    {
        try
        {
            var lines = File.ReadAllLines(stamp);
            return lines.Length == 3 && lines[0] == StampVersion && lines[1] == fingerprint && lines[2].Length == 64 ? lines[2] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the pair for the next process; false when the write failed, which only costs that process a full
    /// hash. A torn or concurrent write fails <see cref="ReadStamp"/>'s shape check and reads as no stamp.
    /// </summary>
    private static bool WriteStamp(string stamp, string fingerprint, string hash)
    {
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllText(stamp, $"{StampVersion}\n{fingerprint}\n{hash}\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Hashes a path to sha256 map the way <see cref="SourceTreeHash(string)"/> describes.</summary>
    internal static string HashLines(IReadOnlyDictionary<string, string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in lines.Keys.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\t{lines[path]}\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Walks up from <paramref name="dir"/> to the checkout root (the directory holding <c>.git</c>, a directory in
    /// the main checkout and a file in a worktree); null when there is none.
    /// </summary>
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
            if (parent is null || parent == current)
            {
                return null;
            }

            current = parent;
        }
    }
}
