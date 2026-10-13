using System.Text.RegularExpressions;

namespace RimGovernor.Tools;

/// <summary>
/// Detects that the tool binary is older than its sources. RimGovernor.cmd publishes the tool into
/// <c>.rimgovernor/tools/&lt;version&gt;/</c> and writes the version into <c>.rimgovernor/tools/current</c> once the
/// publish succeeded; a binary cannot replace itself while running, so a stale one exits with
/// <see cref="StaleExitCode"/> before doing anything and the shim rebuilds and runs the command again.
/// A binary run from anywhere else (dotnet run, the build output) has no <c>current</c> file and is never stale.
/// The sources are <c>tools/rg</c>, every project it references (the brain/ acceptance harness and what it builds on,
/// found by following the <c>ProjectReference</c> closure) and the shared brain build files above them.
/// </summary>
internal static partial class SelfBuild
{
    /// <summary>Reserved: no tool returns it as its own result.</summary>
    public const int StaleExitCode = 75;

    private static readonly string[] SourceExtensions = [".cs", ".csproj", ".props", ".targets", ".editorconfig"];

    public static bool IsStale()
    {
        // <root>/.rimgovernor/tools/<version>/rg.dll
        var binaryDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var toolsDir = Path.GetDirectoryName(binaryDir);
        var stamp = toolsDir is null ? null : Path.Combine(toolsDir, "current");
        if (stamp is null || !File.Exists(stamp))
        {
            return false;
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(toolsDir));
        var sources = root is null ? null : Path.Combine(root, "tools", "rg");
        if (root is null || sources is null || !Directory.Exists(sources))
        {
            return false;
        }
        var built = File.GetLastWriteTimeUtc(stamp);
        if (NewerThan(sources, built))
        {
            return true;
        }
        foreach (var dir in ReferencedProjectDirectories(Path.Combine(sources, "rg.csproj")))
        {
            if (NewerThan(dir, built))
            {
                return true;
            }
        }
        // Shared build files that change every brain project (Directory.Build.props, Directory.Packages.props, ...).
        var brain = Path.Combine(root, "brain");
        return Directory.Exists(brain) && TopLevelNewerThan(brain, built);
    }

    /// <summary>The directories of every project the project file references, transitively (not the project itself).</summary>
    private static List<string> ReferencedProjectDirectories(string projectFile)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(projectFile) };
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(projectFile));
        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            string text;
            try
            {
                text = File.ReadAllText(project);
            }
            catch (IOException)
            {
                continue;
            }
            var baseDir = Path.GetDirectoryName(project) ?? "";
            foreach (Match match in ProjectReference().Matches(text))
            {
                var reference = Path.GetFullPath(Path.Combine(baseDir, match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)));
                if (seen.Add(reference))
                {
                    dirs.Add(Path.GetDirectoryName(reference) ?? baseDir);
                    queue.Enqueue(reference);
                }
            }
        }
        return dirs;
    }

    [GeneratedRegex("""<ProjectReference\s+Include="(?<path>[^"]+)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectReference();

    private static bool TopLevelNewerThan(string dir, DateTime built)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFiles())
        {
            if (SourceExtensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) && entry.LastWriteTimeUtc > built)
            {
                return true;
            }
        }
        return false;
    }

    private static bool NewerThan(string dir, DateTime built)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub)
            {
                if (sub.Name is "bin" or "obj")
                {
                    continue;
                }
                if (NewerThan(sub.FullName, built))
                {
                    return true;
                }
            }
            else if (SourceExtensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) && entry.LastWriteTimeUtc > built)
            {
                return true;
            }
        }
        return false;
    }
}
