using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// RequireCurrentPackage found the installed build's sources differ from the worktree's:
/// rebuild it or set <see cref="StalePackage.AllowStaleModEnv"/>.
/// </summary>
public sealed class StalePackageException : AcceptanceException
{
    public StalePackageException(string message)
        : base(message)
    {
    }
}

/// <summary>The stale-package check run before any game is launched.</summary>
public static class StalePackage
{
    /// <summary>
    /// Skips <see cref="RequireCurrent"/>: set it to run a harness against an installed native build whose sources are
    /// known to differ from the worktree (bisecting the mod against newer code, for instance).
    /// </summary>
    public const string AllowStaleModEnv = "RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD";

    /// <summary>
    /// Fails, before any game is launched, when <paramref name="pkg"/> (the installed Mods/RimGovernor) was built from
    /// native sources that differ from the worktree the harness runs in. A stale build otherwise surfaces minutes later and
    /// obliquely: a missing fixture op, an INVALID_REQUEST refusal, a receipt shape the harness no longer decodes. The
    /// check compares the manifest's sourceTree with <see cref="NativeSourceInputs.SourceTreeHash(string)"/> over the worktree; a
    /// build recorded before sourceTree existed falls back to git (the build revision against the worktree over the same
    /// inputs). It is skipped, and says so in the summary, when there is no manifest, no enclosing checkout, no usable
    /// git, or <see cref="AllowStaleModEnv"/> is set. <paramref name="fixtureOps"/> are the test ops the run's Start calls
    /// (<see cref="Config.FixtureOps"/>): the rebuild hint names their fixtures beside the installed build's, so following
    /// it cannot leave the case's own fixture out. The summary is also the report's installed_package.
    /// </summary>
    public static InstalledPackageSummary RequireCurrent(string pkg, IReadOnlyList<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(fixtureOps);
        var summary = Publish(new InstalledPackageSummary { Path = pkg });
        SetupPackageManifest manifest;
        try
        {
            manifest = SetupPackage.ReadPackageManifest(pkg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Publish(summary with { Skipped = "no manifest: " + e.Message });
        }

        summary = summary with
        {
            Role = manifest.Role,
            Fixtures = manifest.Fixtures,
            SourceRevision = manifest.SourceRevision,
            SourceDirty = manifest.SourceDirty,
        };
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AllowStaleModEnv)))
        {
            return Publish(summary with { Skipped = AllowStaleModEnv + " set" });
        }

        var cwd = Directory.GetCurrentDirectory();
        var repo = NativeSourceInputs.FindRepo(cwd);
        if (repo is null)
        {
            return Publish(summary with { Skipped = "no enclosing checkout under " + cwd });
        }

        summary = summary with { Worktree = repo };
        if (manifest.SourceTree.Length > 0)
        {
            string current;
            try
            {
                current = NativeSourceInputs.SourceTreeHash(repo);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                return Publish(summary with { Skipped = e.Message });
            }

            summary = Publish(summary with { Checked = true, Method = "source_tree", SourceTree = manifest.SourceTree, WorktreeSourceTree = current });
            return string.Equals(current, manifest.SourceTree, StringComparison.Ordinal)
                ? summary
                : throw StaleError(pkg, manifest, repo, fixtureOps, "its native sources differ from this worktree's");
        }

        // An older build: ask git whether the inputs changed since its revision.
        if (manifest.SourceRevision.Length == 0)
        {
            return Publish(summary with { Skipped = "manifest records neither sourceTree nor sourceRevision" });
        }

        var (exit, output) = GitDiffQuiet(repo, manifest.SourceRevision);
        switch (exit)
        {
            case 0:
                return Publish(summary with
                {
                    Checked = true,
                    Method = "git_diff",
                    Note = manifest.SourceDirty ? "built from a dirty tree; uncommitted changes at build time are not compared" : null,
                });
            case 1:
                _ = Publish(summary with { Checked = true, Method = "git_diff" });
                throw StaleError(pkg, manifest, repo, fixtureOps, "native sources changed since revision " + Short(manifest.SourceRevision));
            default:
                return Publish(summary with { Skipped = "git diff: " + output.Trim() + (exit < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" exit status {exit}")) });
        }
    }

    /// <summary>Records <paramref name="summary"/> as the run's installed_package and returns it.</summary>
    private static InstalledPackageSummary Publish(InstalledPackageSummary summary)
    {
        RunState.InstalledPackage = LaunchJson.Node(summary);
        return summary;
    }

    /// <summary>
    /// <c>git -C repo diff --quiet revision -- native roots</c>: the exit status (0 identical, 1 changed, other a failure,
    /// -1 git could not run) and the combined output.
    /// </summary>
    private static (int Exit, string Output) GitDiffQuiet(string repo, string revision)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add(revision);
        psi.ArgumentList.Add("--");
        foreach (var root in NativeSourceInputs.NativeSourceRoots())
        {
            psi.ArgumentList.Add(root);
        }

        try
        {
            using var git = Process.Start(psi);
            if (git is null)
            {
                return (-1, "git did not start");
            }

            var stdout = git.StandardOutput.ReadToEndAsync();
            var stderr = git.StandardError.ReadToEndAsync();
            git.WaitForExit();
            return (git.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, e.Message);
        }
    }

    private static StalePackageException StaleError(string pkg, SetupPackageManifest manifest, string repo, IReadOnlyList<string> fixtureOps, string why)
    {
        var fixtures = "";
        var flags = FixtureFlags(repo, manifest.Fixtures, fixtureOps);
        if (flags.Count > 0)
        {
            fixtures = " -fixtures " + string.Join(',', flags);
        }

        return new StalePackageException(
            $"installed native build is stale for this worktree: {pkg} was built at {Short(manifest.SourceRevision)} and {why}; rebuild it (RimGovernor.cmd native-build -out <fresh dir>{fixtures}, then install the RimGovernor package over {pkg}) or set {AllowStaleModEnv}=1 to run against it anyway");
    }

    /// <summary>
    /// The sorted -Fixture list a rebuild for this run needs: <paramref name="installed"/> (the manifest's fixtures, so
    /// the build keeps serving the cases still present in the checkout) plus the fixtures under repo/scripts/fixtures that
    /// register <paramref name="fixtureOps"/>. Ops no fixture registers, or an unreadable fixture root, add nothing.
    /// </summary>
    public static IReadOnlyList<string> FixtureFlags(string repo, IEnumerable<string> installed, IEnumerable<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(fixtureOps);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ISet<string>? known = null;
        try
        {
            known = FixtureInputs.FixtureNames(repo);
        }
        catch (AcceptanceException e)
        {
            Console.Error.WriteLine($"discover fixture names under {repo}: {e.Message}; rebuild hint retains installed names");
        }

        foreach (var name in installed)
        {
            if (name.Length > 0 && (known is null || known.Contains(name)))
            {
                seen.Add(name);
            }
        }

        try
        {
            foreach (var name in FixtureInputs.FixtureClasses(repo, fixtureOps))
            {
                if (name.Length > 0)
                {
                    seen.Add(name);
                }
            }
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"discover fixture operations under {repo}: {e.Message}; rebuild hint may be incomplete");
        }

        return [.. seen.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The " -Fixture &lt;classes&gt;" a discovery failure for <paramref name="op"/> should suggest, from the checkout
    /// enclosing the working directory; empty when no fixture there registers the op or there is no checkout.
    /// </summary>
    public static string FixtureBuildHint(string op)
    {
        var repo = NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory());
        if (repo is null)
        {
            return "";
        }

        var flags = FixtureFlags(repo, [], [op]);
        return flags.Count == 0 ? "" : " -Fixture " + string.Join(',', flags);
    }

    /// <summary>The revision shortened to 12 characters, or a phrase for none.</summary>
    internal static string Short(string revision)
    {
        if (revision.Length > 12)
        {
            return revision[..12];
        }

        return revision.Length == 0 ? "an unknown revision" : revision;
    }
}
