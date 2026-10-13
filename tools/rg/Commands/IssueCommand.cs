using System.Globalization;
using System.Text;
using System.Text.Json;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// Prints a GitHub issue with its comments as compact markdown, in one call, however long the thread is (Go:
/// <c>cmd/issue</c>).
/// <code>RimGovernor.cmd issue [-o file] [-limit bytes] [-last n] number...</code>
/// Reads the issue through <c>gh issue view --json</c>, so it works where <c>gh issue view --comments</c> does not:
/// with stdout redirected that form prints only the comments, and nothing at all on an issue without any. Output is
/// the title line, labels, the body, then every comment as <c>### author date</c> sections. The tool harness caps a
/// command's output near 30k characters, so past -limit the remainder is cut at a comment boundary and the tail names
/// the file holding the full text (-o, default issue-N.md in the scratchpad or the OS temp directory). -last keeps
/// only the newest n comments, for catching up on a thread already read.
/// </summary>
internal static class IssueCommand
{
    private static readonly byte[] CommentMarker = Encoding.UTF8.GetBytes("\n### ");
    private static readonly byte[] NewLine = [(byte)0x0A];

    private const string Fields = "number,title,state,url,body,author,labels,createdAt,comments";

    private sealed record Comment(string Author, DateTimeOffset CreatedAt, string Body);

    private sealed record Issue(
        int Number,
        string Title,
        string State,
        string Url,
        string Body,
        string Author,
        IReadOnlyList<string> Labels,
        DateTimeOffset CreatedAt,
        IReadOnlyList<Comment> Comments);

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "o", "limit", "last" });
        if (flags.HelpRequested || flags.Positional.Count == 0)
        {
            var usage = "usage: issue [-o file] [-limit bytes] [-last n] <number>...";
            if (flags.HelpRequested)
            {
                Log.Info(usage);
                Log.Info("  -o      file to hold the full rendering (default issue-<n>.md under $CLAUDE_SCRATCHPAD or the OS temp dir)");
                Log.Info("  -limit  bytes to print before deferring the rest to the file (default 25000)");
                Log.Info("  -last   print only the newest n comments (0 = all)");
                return 0;
            }
            throw new UsageException(usage);
        }
        var output = flags.String("o");
        var limit = flags.Int("limit", 25000);
        var last = flags.Int("last");
        foreach (var number in flags.Positional)
        {
            try
            {
                await ShowAsync(number, output, limit, last, ct).ConfigureAwait(false);
            }
            catch (ToolException e)
            {
                throw new ToolException($"{number}: {e.Message}", e);
            }
        }
        return 0;
    }

    private static async Task ShowAsync(string number, string output, int limit, int last, CancellationToken ct)
    {
        var raw = await Git.GhAsync(Directory.GetCurrentDirectory(), ct, "issue", "view", number, "--json", Fields).ConfigureAwait(false);
        var issue = Parse(raw);
        var full = Render(issue, 0);
        if (output.Length == 0)
        {
            output = Path.Combine(Scratchpad(), $"issue-{issue.Number}.md");
        }
        await File.WriteAllBytesAsync(output, full, ct).ConfigureAwait(false);
        var shown = last > 0 ? Render(issue, last) : full;
        using var stdout = Console.OpenStandardOutput();
        if (shown.Length <= limit)
        {
            await stdout.WriteAsync(shown, ct).ConfigureAwait(false);
            return;
        }
        // Cut at the last comment heading that fits, so the reader never sees half a comment; the pointer tells them
        // where the rest lives.
        byte[] marker = CommentMarker;
        var cut = shown.AsSpan(0, limit).LastIndexOf(marker);
        if (cut <= 0)
        {
            cut = limit;
        }
        await stdout.WriteAsync(shown.AsMemory(0, cut), ct).ConfigureAwait(false);
        var rest = Count(shown.AsSpan(cut), marker);
        var line = Count(shown.AsSpan(0, cut), "\n"u8) + 1;
        var tail = $"\n\n[... {rest} more comment(s), {shown.Length - cut} bytes: read {output} from line {line}]\n";
        await stdout.WriteAsync(Encoding.UTF8.GetBytes(tail), ct).ConfigureAwait(false);
    }

    private static int Count(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        while (true)
        {
            var at = haystack.IndexOf(needle);
            if (at < 0)
            {
                return count;
            }
            count++;
            haystack = haystack[(at + needle.Length)..];
        }
    }

    private static Issue Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var labels = root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => Str(x, "name")).ToList()
                : [];
            var comments = root.TryGetProperty("comments", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => new Comment(Login(x), Date(x), Str(x, "body"))).ToList()
                : [];
            return new Issue(
                root.GetProperty("number").GetInt32(),
                Str(root, "title"),
                Str(root, "state"),
                Str(root, "url"),
                Str(root, "body"),
                Login(root),
                labels,
                Date(root),
                comments);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException("decode gh output: " + e.Message, e);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Login(JsonElement e) =>
        e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : "";

    private static DateTimeOffset Date(JsonElement e) =>
        DateTimeOffset.TryParse(Str(e, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : default;

    private static byte[] Render(Issue issue, int last)
    {
        var b = new StringBuilder();
        var ic = CultureInfo.InvariantCulture;
        b.Append(ic, $"# #{issue.Number} {issue.Title}\n\n");
        b.Append(ic, $"{issue.State.ToLowerInvariant()} · {issue.Author} · {issue.CreatedAt:yyyy-MM-dd} · {issue.Comments.Count} comments · {issue.Url}\n\n");
        if (issue.Labels.Count > 0)
        {
            b.Append(ic, $"labels: {string.Join(", ", issue.Labels)}\n\n");
        }
        var comments = issue.Comments;
        if (last > 0 && last < comments.Count)
        {
            b.Append(ic, $"(body and {comments.Count - last} older comment(s) omitted)\n\n");
            comments = [.. comments.Skip(comments.Count - last)];
        }
        else
        {
            b.Append(issue.Body.Trim()).Append("\n\n");
        }
        foreach (var c in comments)
        {
            b.Append(ic, $"### {c.Author} {c.CreatedAt:yyyy-MM-dd HH:mm}\n\n{c.Body.Trim()}\n\n");
        }
        return Encoding.UTF8.GetBytes(b.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string Scratchpad()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_SCRATCHPAD");
        return string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir;
    }
}
