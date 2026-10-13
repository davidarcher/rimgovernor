namespace RimGovernor.Tools;

/// <summary>
/// Detects that the tool binary is older than its sources. RimGovernor.cmd publishes the tool into
/// <c>.rimgovernor/tools/&lt;version&gt;/</c> and writes the version into <c>.rimgovernor/tools/current</c> once the
/// publish succeeded; a binary cannot replace itself while running, so a stale one exits with
/// <see cref="StaleExitCode"/> before doing anything and the shim rebuilds and runs the command again.
/// A binary run from anywhere else (dotnet run, the build output) has no <c>current</c> file and is never stale.
/// </summary>
internal static class SelfBuild
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
        if (sources is null || !Directory.Exists(sources))
        {
            return false;
        }
        var built = File.GetLastWriteTimeUtc(stamp);
        return NewerThan(sources, built);
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
