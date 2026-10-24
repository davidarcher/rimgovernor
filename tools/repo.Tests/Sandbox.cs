using System.Diagnostics;
using RimGovernor.Tools.Common;
using RimGovernor.Tools.Workflow.Commands;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>
/// A throwaway repository laid out like the real one: a bare <c>origin</c>, a <c>main</c> clone with main checked out
/// (the main checkout), and a <c>task</c> worktree of it on branch <c>task</c>. Git runs with no global or system
/// config, so a developer's hooks, signing or aliases never reach the lane under test.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    static Sandbox()
    {
        var empty = Path.Combine(Path.GetTempPath(), "rg-land-tests-gitconfig");
        File.WriteAllText(empty, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Lane Test");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "lane@test.invalid");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Lane Test");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "lane@test.invalid");
    }

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "rg-land-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Git(Root, "init", "-q", "--bare", "-b", "main", Origin);
        Git(Root, "clone", "-q", Origin, Main);
        Write(Main, "README.md", "seed\n");
        Write(Main, "shared.txt", "one\ntwo\nthree\n");
        Commit(Main, "Seed");
        Git(Main, "push", "-q", "origin", "main");
        Git(Main, "worktree", "add", "-q", "-b", "task", Worktree, "main");
    }

    public string Root { get; }

    public string Origin => Path.Combine(Root, "origin.git");

    public string Main => Path.Combine(Root, "main");

    public string Worktree => Path.Combine(Root, "task");

    public LandCommand.Options Options() => new() { Worktree = Worktree };

    public Task LandAsync(LandCommand.Options? options = null) => LandCommand.LandAsync(options ?? Options(), CancellationToken.None);

    /// <summary>Another developer's landing: a fresh clone commits <paramref name="file"/> and pushes it to origin.</summary>
    public string PushFromElsewhere(string file, string content, string subject)
    {
        var other = Path.Combine(Root, "other-" + Guid.NewGuid().ToString("N")[..6]);
        Git(Root, "clone", "-q", Origin, other);
        Write(other, file, content);
        Commit(other, subject);
        Git(other, "push", "-q", "origin", "main");
        return Git(other, "rev-parse", "HEAD");
    }

    public static void Write(string dir, string file, string content)
    {
        var path = Path.Combine(dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void Commit(string dir, string subject)
    {
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", subject);
    }

    public string OriginMain() => Git(Origin, "rev-parse", "main");

    public static string Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} in {dir}: exit {process.ExitCode}: {stderr}");
        }
        return stdout.Result.Trim();
    }

    public void Dispose()
    {
        // Git marks pack files read-only; clear that so the directory can go.
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Asserts the lane refused with a <see cref="ToolException"/> and returns it.</summary>
internal static class SandboxAssert
{
    public static async Task<ToolException> RefusedAsync(Func<Task> land) => await Assert.ThrowsAsync<ToolException>(land);
}
