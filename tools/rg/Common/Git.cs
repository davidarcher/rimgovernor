namespace RimGovernor.Tools.Common;

/// <summary>git and gh helpers over <see cref="Proc"/>.</summary>
internal static class Git
{
    /// <summary>Runs git and returns trimmed stdout; a non-zero exit throws.</summary>
    public static async Task<string> RunAsync(string dir, CancellationToken ct, params string[] args) =>
        (await OutputAsync(dir, ct, args).ConfigureAwait(false)).Trim();

    /// <summary>Runs git and returns stdout exactly; a non-zero exit throws.</summary>
    public static Task<string> OutputAsync(string dir, CancellationToken ct, params string[] args) => Proc.OutputAsync(dir, "git", args, ct);

    /// <summary>True when git exits 0 (the Go <c>err == nil</c> predicate form, for example <c>diff --quiet</c>).</summary>
    public static async Task<bool> SucceedsAsync(string dir, CancellationToken ct, params string[] args) =>
        (await Proc.CaptureAsync(dir, "git", args, ct).ConfigureAwait(false)).ExitCode == 0;

    /// <summary>Non-empty output lines, or none when the output is blank.</summary>
    public static async Task<IReadOnlyList<string>> LinesAsync(string dir, CancellationToken ct, params string[] args)
    {
        var text = (await OutputAsync(dir, ct, args).ConfigureAwait(false)).Trim();
        return text.Length == 0 ? [] : text.Split('\n');
    }

    /// <summary>Runs gh and returns stdout; a non-zero exit throws.</summary>
    public static Task<string> GhAsync(string dir, CancellationToken ct, params string[] args) => Proc.OutputAsync(dir, "gh", args, ct);
}

internal static class Repo
{
    /// <summary>
    /// Walks up from <paramref name="dir"/> to the checkout root: the directory holding .git, a directory in the main
    /// checkout and a file in a worktree.
    /// </summary>
    public static string? Find(string dir)
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

    public static string Require(string dir) =>
        Find(dir) ?? throw new ToolException($"not inside a git checkout: {dir}");
}
