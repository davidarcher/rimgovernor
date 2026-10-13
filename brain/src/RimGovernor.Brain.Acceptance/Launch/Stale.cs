using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// RequireCurrentPackage found the installed build's sources differ from the worktree's (Go <c>ErrStalePackage</c>):
/// rebuild it or set <see cref="StalePackage.AllowStaleModEnv"/>.
/// </summary>
public sealed class StalePackageException : AcceptanceException
{
    public StalePackageException(string message)
        : base(message)
    {
    }
}

/// <summary>The stale-package check run before any game is launched (Go <c>stale.go</c>).</summary>
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
    /// check compares the manifest's sourceTree with <see cref="NativeSourceInputs.SourceTreeHash"/> over the worktree; a
    /// build recorded before sourceTree existed falls back to git (the build revision against the worktree over the same
    /// inputs). It is skipped, and says so in the summary, when there is no manifest, no enclosing checkout, no usable
    /// git, or <see cref="AllowStaleModEnv"/> is set. <paramref name="fixtureOps"/> are the test ops the run's Start calls
    /// (<see cref="Config.FixtureOps"/>): the rebuild hint names their fixtures beside the installed build's, so following
    /// it cannot leave the case's own fixture out. The summary is also the report's installed_package.
    /// </summary>
    public static JsonObject RequireCurrent(string pkg, IReadOnlyList<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(fixtureOps);
        var summary = new JsonObject { ["path"] = pkg, ["checked"] = false };
        RunState.InstalledPackage = summary;
        SetupPackageManifest manifest;
        try
        {
            manifest = SetupPackage.ReadPackageManifest(pkg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            summary["skipped"] = "no manifest: " + e.Message;
            return summary;
        }

        summary["role"] = manifest.Role;
        summary["fixtures"] = new JsonArray([.. manifest.Fixtures.Select(f => (JsonNode?)JsonValue.Create(f))]);
        summary["source_revision"] = manifest.SourceRevision;
        summary["source_dirty"] = manifest.SourceDirty;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AllowStaleModEnv)))
        {
            summary["skipped"] = AllowStaleModEnv + " set";
            return summary;
        }

        var cwd = Directory.GetCurrentDirectory();
        var repo = NativeSourceInputs.FindRepo(cwd);
        if (repo is null)
        {
            summary["skipped"] = "no enclosing checkout under " + cwd;
            return summary;
        }

        summary["worktree"] = repo;
        if (manifest.SourceTree.Length > 0)
        {
            string current;
            try
            {
                current = NativeSourceInputs.SourceTreeHash(repo);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                summary["skipped"] = e.Message;
                return summary;
            }

            summary["checked"] = true;
            summary["method"] = "source_tree";
            summary["source_tree"] = manifest.SourceTree;
            summary["worktree_source_tree"] = current;
            if (!string.Equals(current, manifest.SourceTree, StringComparison.Ordinal))
            {
                throw StaleError(pkg, manifest, repo, fixtureOps, "its native sources differ from this worktree's");
            }

            return summary;
        }

        // An older build: ask git whether the inputs changed since its revision.
        if (manifest.SourceRevision.Length == 0)
        {
            summary["skipped"] = "manifest records neither sourceTree nor sourceRevision";
            return summary;
        }

        var (exit, output) = GitDiffQuiet(repo, manifest.SourceRevision);
        switch (exit)
        {
            case 0:
                summary["checked"] = true;
                summary["method"] = "git_diff";
                if (manifest.SourceDirty)
                {
                    summary["note"] = "built from a dirty tree; uncommitted changes at build time are not compared";
                }

                return summary;
            case 1:
                summary["checked"] = true;
                summary["method"] = "git_diff";
                throw StaleError(pkg, manifest, repo, fixtureOps, "native sources changed since revision " + Short(manifest.SourceRevision));
            default:
                summary["skipped"] = "git diff: " + output.Trim() + (exit < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" exit status {exit}"));
                return summary;
        }
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
            fixtures = " -Fixture " + string.Join(',', flags);
        }

        return new StalePackageException(
            $"installed native build is stale for this worktree: {pkg} was built at {Short(manifest.SourceRevision)} and {why}; rebuild it (pwsh scripts/build_native_mod.ps1{fixtures} -OutputRoot <fresh dir>, then install the RimGovernor package over {pkg}) or set {AllowStaleModEnv}=1 to run against it anyway");
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
        catch (AcceptanceException)
        {
            // An unreadable fixture root keeps every installed name.
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
            // Ops no fixture registers add nothing.
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
