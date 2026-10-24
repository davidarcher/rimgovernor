using System.Globalization;
using System.Text;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// Resolves merge conflicts in the shrink-only gate baselines (<c>brain/analyzers/baseline/*.txt</c>) line by line, so
/// two landings that each shrank a baseline never stop the landing lane. Every baseline is a set of entries that
/// only ever shrinks: an entry stays when both sides keep it or one side added it, and goes when either side
/// deleted it. In rule9.txt, whose entries are counted (<c>file|Kind|count</c>), a kept entry takes the smaller
/// count. Header comments come from our side.
/// </summary>
internal static class BaselineMerge
{
    internal const string Dir = "brain/analyzers/baseline/";

    /// <summary>The one counted baseline (Rule9Unknowns.Counted).</summary>
    private const string Counted = "rule9.txt";

    /// <summary>
    /// Resolves an in-progress merge whose conflicts are all baseline files and commits it. False, touching nothing,
    /// when there is no conflict to resolve or any conflicted path lies outside the baselines.
    /// </summary>
    public static async Task<bool> ResolveAsync(string worktree, CancellationToken ct)
    {
        var conflicted = (await Git.LinesAsync(worktree, ct, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false))
            .Select(path => path.TrimEnd('\r'))
            .Where(path => path.Length > 0)
            .ToList();
        if (conflicted.Count == 0 || conflicted.Any(path => !path.StartsWith(Dir, StringComparison.Ordinal) || path.IndexOf('/', Dir.Length) >= 0))
        {
            return false;
        }
        foreach (var path in conflicted)
        {
            var merged = Merge(
                await StageAsync(worktree, 1, path, ct).ConfigureAwait(false),
                await StageAsync(worktree, 2, path, ct).ConfigureAwait(false),
                await StageAsync(worktree, 3, path, ct).ConfigureAwait(false),
                counted: path.EndsWith("/" + Counted, StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(worktree, path), merged, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await Git.RunAsync(worktree, ct, "add", "--", path).ConfigureAwait(false);
        }
        await Git.RunAsync(worktree, ct, "commit", "--no-edit", "-q").ConfigureAwait(false);
        Log.Info($"resolved baseline conflicts line by line: {string.Join(", ", conflicted)}");
        return true;
    }

    /// <summary>The three-way merge of one baseline's text; an absent side is empty.</summary>
    internal static string Merge(string baseText, string ours, string theirs, bool counted)
    {
        var start = Entries(baseText, counted);
        var mine = Entries(ours, counted);
        var other = Entries(theirs, counted);
        var lines = Lines(ours).Where(line => line.StartsWith('#')).ToList();
        foreach (var (key, count) in mine)
        {
            if (other.TryGetValue(key, out var otherCount))
            {
                lines.Add(Line(key, Math.Min(count, otherCount), counted));
            }
            else if (!start.ContainsKey(key))
            {
                lines.Add(Line(key, count, counted));
            }
        }
        foreach (var (key, count) in other)
        {
            if (!mine.ContainsKey(key) && !start.ContainsKey(key))
            {
                lines.Add(Line(key, count, counted));
            }
        }
        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    /// <summary>Entries in file order: the whole line (count -1), or for a counted baseline the line up to its count.</summary>
    private static Dictionary<string, int> Entries(string text, bool counted)
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(text).Where(line => !line.StartsWith('#')))
        {
            var bar = line.LastIndexOf('|');
            if (counted && bar > 0 && int.TryParse(line.AsSpan(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                entries.TryAdd(line[..bar], count);
            }
            else
            {
                entries.TryAdd(line, -1);
            }
        }
        return entries;
    }

    private static string Line(string key, int count, bool counted) =>
        count >= 0 ? key + "|" + count.ToString(CultureInfo.InvariantCulture) : key;

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0);

    private static async Task<string> StageAsync(string worktree, int stage, string path, CancellationToken ct)
    {
        var show = await Proc.CaptureAsync(worktree, "git", ["show", $":{stage}:{path}"], ct).ConfigureAwait(false);
        return show.ExitCode == 0 ? show.Stdout : "";
    }
}
