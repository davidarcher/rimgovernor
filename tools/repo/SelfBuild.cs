using System.Text.RegularExpressions;

namespace RimGovernor.Tools.Workflow;

/// <summary>
/// Detects that a published binary is older than its sources, by file modification time against a stamp file written
/// after the publish succeeded. Three binaries use it:
/// <list type="bullet">
/// <item>The front door itself (tools/repo). RimGovernor.cmd publishes it into <c>.rimgovernor/tools/&lt;version&gt;/</c>
/// and writes the version into <c>.rimgovernor/tools/current</c>; a binary cannot replace itself while running, so a
/// stale one exits with <see cref="StaleExitCode"/> before doing anything and the shim rebuilds and runs the command
/// again. A binary run from anywhere else (dotnet run, the build output) has no <c>current</c> file and is never
/// stale.</item>
/// <item>The brain tool host (tools/rg), which <see cref="BrainHost"/> republishes into <c>.rimgovernor/rg/</c>.</item>
/// <item>The launcher (<c>launcher-stale</c>). RimGovernor.cmd's double-click path publishes <c>tools/launcher</c> only
/// when this reports it stale, then writes <c>.rimgovernor/launcher-build/current</c>.</item>
/// </list>
/// A project's sources are its directory, every project it references (found by following the <c>ProjectReference</c>
/// closure) and the shared build files (<c>Directory.Build.props</c>, ...) in the directories above each of them.
/// </summary>
internal static partial class SelfBuild
{
    /// <summary>
    /// Reserved: no tool returns it as its own result, except <c>launcher-stale</c>, which RimGovernor.cmd runs on the
    /// published binary directly (not through the rebuild shim) after the tool host is known to be current.
    /// </summary>
    public const int StaleExitCode = 75;

    private static readonly string[] SourceExtensions = [".cs", ".csproj", ".props", ".targets", ".editorconfig", ".js", ".css"];

    /// <summary>The launcher also embeds its page, <c>tools/launcher/ui/index.html</c>.</summary>
    private static readonly string[] LauncherExtensions = [".html"];

    public static bool IsStale()
    {
        // <root>/.rimgovernor/tools/<version>/repo.dll
        var binaryDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var toolsDir = Path.GetDirectoryName(binaryDir);
        var stamp = toolsDir is null ? null : Path.Combine(toolsDir, "current");
        if (stamp is null || !File.Exists(stamp))
        {
            return false;
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(toolsDir));
        var project = root is null ? null : Path.Combine(root, "tools", "repo", "repo.csproj");
        if (root is null || project is null || !File.Exists(project))
        {
            return false;
        }
        return IsStale(root, project, [], stamp);
    }

    /// <summary>
    /// <c>RimGovernor.cmd launcher-stale</c>: <see cref="StaleExitCode"/> when the launcher needs a publish (no stamp,
    /// no root <c>RimGovernorLauncher.exe</c>, or a source newer than the stamp), 0 when the root exe is current.
    /// <paramref name="locked"/> (<c>-locked</c>): the caller holds the launcher build lock and publishes on a stale
    /// answer, so a stale launcher's torn reference assemblies are cleared first (<see cref="TornRefAssemblies"/>).
    /// </summary>
    public static int LauncherStale(bool locked)
    {
        var root = Common.Repo.Find(AppContext.BaseDirectory);
        if (root is null)
        {
            return StaleExitCode; // cannot tell: publish, as before the check existed
        }
        var project = Path.Combine(root, "tools", "launcher", "launcher.csproj");
        var stamp = Path.Combine(root, ".rimgovernor", "launcher-build", "current");
        var stale = !File.Exists(Path.Combine(root, "RimGovernorLauncher.exe")) || IsStale(root, project, LauncherExtensions, stamp);
        if (stale && locked)
        {
            TornRefAssemblies.Clear(Path.Combine(root, ".rimgovernor", "launcher-build", "artifacts", "obj"));
        }
        return stale ? StaleExitCode : 0;
    }

    /// <summary>
    /// Whether any source of <paramref name="projectFile"/> (its directory, its project references, the shared build
    /// files above them), counting <paramref name="extraExtensions"/> as sources too, is newer than <paramref name="stamp"/>.
    /// A missing stamp is stale.
    /// </summary>
    public static bool IsStale(string root, string projectFile, string[] extraExtensions, string stamp)
    {
        if (!File.Exists(stamp))
        {
            return true;
        }
        string[] extensions = [.. SourceExtensions, .. extraExtensions];
        var built = File.GetLastWriteTimeUtc(stamp);
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? root;
        List<string> dirs = [projectDir, .. ReferencedProjectDirectories(projectFile)];
        if (dirs.Any(dir => NewerThan(dir, built, extensions)))
        {
            return true;
        }
        // Shared build files (Directory.Build.props, Directory.Packages.props, ...) between each project and the root.
        var top = Path.GetFullPath(root).TrimEnd('\\', '/');
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            for (var up = Path.GetDirectoryName(dir); up is not null && up.Length > top.Length; up = Path.GetDirectoryName(up))
            {
                ancestors.Add(up);
            }
        }
        return ancestors.Any(dir => TopLevelNewerThan(dir, built, extensions));
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

    private static bool TopLevelNewerThan(string dir, DateTime built, string[] extensions)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFiles())
        {
            if (extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) && entry.LastWriteTimeUtc > built)
            {
                return true;
            }
        }
        return false;
    }

    private static bool NewerThan(string dir, DateTime built, string[] extensions)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub)
            {
                if (sub.Name is "bin" or "obj")
                {
                    continue;
                }
                if (NewerThan(sub.FullName, built, extensions))
                {
                    return true;
                }
            }
            else if (extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) && entry.LastWriteTimeUtc > built)
            {
                return true;
            }
        }
        return false;
    }
}
