using System.Diagnostics;
using System.Runtime.InteropServices;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow;

/// <summary>
/// The brain tool host (tools/rg) as RimGovernor.cmd sees it: published ReadyToRun into
/// <c>.rimgovernor/rg/&lt;version&gt;/</c> when its sources (its project, every brain project it references and their
/// shared build files) are newer than the last publish, then run with this process's console. A brain compile error
/// therefore fails only these tools; land, test and issue keep working.
/// </summary>
internal static class BrainHost
{
    /// <summary>The subcommands tools/rg implements.</summary>
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "acceptance", "remoteaccept", "remotebundle", "thoughtaudit", "colonyreview", "pawnactivity", "forecastresiduals", "launch-phases", "native-build",
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var root = Repo.Require(AppContext.BaseDirectory);
        var exe = await EnsureCurrentAsync(root, ct).ConfigureAwait(false);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new ToolException($"{exe}: did not start");
        // The child shares the console, so Ctrl+C reaches it directly; wait for it to finish its own shutdown.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>The current published executable, publishing it first under the build lock when it is stale.</summary>
    private static async Task<string> EnsureCurrentAsync(string root, CancellationToken ct)
    {
        var dir = Path.Combine(root, ".rimgovernor", "rg");
        var project = Path.Combine(root, "tools", "rg", "rg.csproj");
        var stamp = Path.Combine(dir, "current");
        if (Current(dir) is { } ready && !SelfBuild.IsStale(root, project, [], stamp))
        {
            return ready;
        }
        using var buildLock = await FileLock.AcquireAsync(
                Path.Combine(dir, "build.lock"),
                "building tools/rg",
                TimeSpan.FromMinutes(10),
                TimeSpan.FromSeconds(1),
                _ => { },
                ct)
            .ConfigureAwait(false);
        // Another invocation may have published while this one waited.
        if (Current(dir) is { } built && !SelfBuild.IsStale(root, project, [], stamp))
        {
            return built;
        }
        var previous = File.Exists(stamp) ? (await File.ReadAllTextAsync(stamp, ct).ConfigureAwait(false)).Trim() : "";
        var version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var output = Path.Combine(dir, version);
        await Console.Error.WriteLineAsync("RimGovernor.cmd: building the brain tool host (tools/rg) ...").ConfigureAwait(false);
        TornRefAssemblies.Clear(Path.Combine(dir, "build", "obj"));
        // Its own artifacts root: the brain build may be writing the same projects' bin/obj at this moment.
        var publish = await Proc.CaptureAsync(
                root,
                "dotnet",
                [
                    "publish", project, "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier, "--self-contained", "false",
                    "-p:PublishReadyToRun=true", "-p:NodeReuse=false", "-p:UseArtifactsOutput=true",
                    "-p:ArtifactsPath=" + Path.Combine(dir, "build"), "--nologo", "-v", "q", "-o", output,
                ],
                ct)
            .ConfigureAwait(false);
        if (publish.ExitCode != 0)
        {
            throw new ToolException($"building the brain tool host (tools/rg) failed:\n{publish.Stdout}{publish.Stderr}");
        }
        var temp = stamp + ".tmp";
        await File.WriteAllTextAsync(temp, version + "\n", ct).ConfigureAwait(false);
        File.Move(temp, stamp, overwrite: true);
        // Running copies keep their own version directory; drop every one but the new and the previous (best effort).
        foreach (var old in Directory.EnumerateDirectories(dir, "v*"))
        {
            var name = Path.GetFileName(old);
            if (name != version && name != previous)
            {
                try
                {
                    Directory.Delete(old, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        return Executable(output);
    }

    private static string? Current(string dir)
    {
        var stamp = Path.Combine(dir, "current");
        if (!File.Exists(stamp))
        {
            return null;
        }
        var exe = Executable(Path.Combine(dir, File.ReadAllText(stamp).Trim()));
        return File.Exists(exe) ? exe : null;
    }

    private static string Executable(string dir) => Path.Combine(dir, OperatingSystem.IsWindows() ? "rg.exe" : "rg");
}
