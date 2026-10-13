using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Knows which checked-in files an acceptance harness run and the native mod build depend on, and hashes them (Go
/// <c>inputs/sources.go</c>). It has no game or transport dependency so the tools that only need the hashes (the
/// verified, land and affected commands) stay cheap.
/// </summary>
public static partial class NativeSourceInputs
{
    /// <summary>
    /// One rule of the source copy build_native_mod.ps1 makes under build/source: which repository path it takes,
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

    /// <summary>Mirrors build_native_mod.ps1's copy loops; keep the two in step when the build takes a new input.</summary>
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
        new("brain/src/RimGovernor.Brain.Domain", "brain/src/RimGovernor.Brain.Domain", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Contracts", "brain/src/RimGovernor.Brain.Contracts", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.NativeFacade", "brain/src/RimGovernor.Brain.NativeFacade", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.HttpChannel", "brain/src/RimGovernor.Brain.HttpChannel", NotBuildOutput),
        new("brain/src/RimGovernor.Brain.Telemetry", "brain/src/RimGovernor.Brain.Telemetry", NotBuildOutput),
        new("scripts/build_native_mod.ps1", "scripts/build_native_mod.ps1"),
        new("scripts/fixtures", "scripts/fixtures", FixtureSource),
        new("THIRD_PARTY.md", "THIRD_PARTY.md"),
    ];

    /// <summary>The paths build_native_mod.ps1 copies from, repo-relative with forward slashes (the same rules <see cref="SourceTreeHash"/> walks).</summary>
    public static IReadOnlyList<string> NativeSourceRoots() => [.. Inputs.Select(input => input.Repo)];

    /// <summary>
    /// The hash build_native_mod.ps1 records as sourceTree: over the sorted list of the build's copied source files,
    /// one <c>"&lt;path&gt;\t&lt;sha256&gt;\n"</c> line each with the path relative to the copy root (forward
    /// slashes), it is the hex SHA-256 of those lines. Computed over the worktree at <paramref name="repo"/>, it
    /// equals the manifest's when the installed build was made from these exact sources.
    /// </summary>
    public static string SourceTreeHash(string repo)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in Inputs)
        {
            var root = Path.Combine(repo, input.Repo.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(root))
            {
                lines[input.Copy] = FileHash(root);
                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new AcceptanceException($"native build input {input.Repo}: not found");
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (input.Keep is not null && !input.Keep(relative))
                {
                    continue;
                }

                lines[input.Copy + "/" + relative] = FileHash(path);
            }
        }

        return HashLines(lines);
    }

    /// <summary>Hashes a path to sha256 map the way <see cref="SourceTreeHash"/> describes.</summary>
    private static string HashLines(Dictionary<string, string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in lines.Keys.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\t{lines[path]}\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

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
