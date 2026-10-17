using System.Text.RegularExpressions;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>A ThoughtWorker class's decompiled source and the class it derives from.</summary>
internal sealed record WorkerSource(string Src, string Base);

/// <summary>A place in the game's code that grants a memory thought: <c>Type.Method</c> and the method's text.</summary>
internal sealed record MemorySite(string Site, string Body);

/// <summary>Scans the decompiled game sources for ThoughtWorker classes and thought grant sites.</summary>
internal static partial class ThoughtSources
{
    [GeneratedRegex(@"\bThoughtDefOf\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex DefOf();

    [GeneratedRegex("\\b(?:GetNamed|GetNamedSilentFail|Named)\\(\"(\\w+)\"", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex Named();

    [GeneratedRegex(@"\bclass\s+(\w+)\s*(?::\s*(\w+))?", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex Class();

    [GeneratedRegex(@"^\t[A-Za-z_].*\(", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex MethodLine();

    /// <summary>
    /// Walks the decompiled tree: returns each ThoughtWorker* class's source by class name and every grant site by def
    /// name.
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerSource> Workers, IReadOnlyDictionary<string, List<MemorySite>> Sites) Scan(
        string root,
        IReadOnlySet<string> names)
    {
        var workers = new Dictionary<string, WorkerSource>(StringComparer.Ordinal);
        var sites = new Dictionary<string, List<MemorySite>>(StringComparer.Ordinal);
        foreach (var path in ThoughtDefs.SortedFiles(root, ".cs"))
        {
            var file = Path.GetFileNameWithoutExtension(path);
            if (file == "ThoughtDefOf")
            {
                continue;
            }
            var src = File.ReadAllText(path);
            if (file.StartsWith("ThoughtWorker", StringComparison.Ordinal))
            {
                var baseClass = "";
                foreach (Match m in Class().Matches(src))
                {
                    if (m.Groups[1].Value == file)
                    {
                        baseClass = m.Groups[2].Value;
                        break;
                    }
                }
                workers[file] = new WorkerSource(src, baseClass);
            }
            if (!src.Contains("ThoughtDefOf.", StringComparison.Ordinal)
                && !src.Contains("Named(\"", StringComparison.Ordinal)
                && !src.Contains("NamedSilentFail(\"", StringComparison.Ordinal))
            {
                continue;
            }
            var lines = src.Split('\n');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var hits = DefOf().Matches(lines[i]).Select(m => m.Groups[1].Value)
                    .Concat(Named().Matches(lines[i]).Select(m => m.Groups[1].Value));
                foreach (var name in hits)
                {
                    if (!names.Contains(name))
                    {
                        continue;
                    }
                    var (method, body) = EnclosingMethod(lines, i);
                    var site = method.Length > 0 ? file + "." + method : file;
                    if (!seen.Add(name + "|" + site))
                    {
                        continue;
                    }
                    if (!sites.TryGetValue(name, out var list))
                    {
                        list = [];
                        sites[name] = list;
                    }
                    list.Add(new MemorySite(site, body));
                }
            }
        }
        return (workers, sites);
    }

    /// <summary>
    /// A worker class's source concatenated with its ThoughtWorker_* base classes (the plain ThoughtWorker is not read);
    /// null when the class is not among the scanned sources.
    /// </summary>
    public static string? WorkerSourceOf(IReadOnlyDictionary<string, WorkerSource> workers, string worker)
    {
        var text = new System.Text.StringBuilder();
        var found = false;
        var current = worker;
        for (var depth = 0; depth < 8 && current.Length > 0 && current != "ThoughtWorker"; depth++)
        {
            if (!workers.TryGetValue(current, out var w))
            {
                break;
            }
            found = true;
            text.Append(w.Src);
            current = w.Base;
        }
        return found ? text.ToString() : null;
    }

    private static bool IsMethodLine(string line) =>
        MethodLine().IsMatch(line) && (!line.Trim().EndsWith(';') || line.Contains("=>", StringComparison.Ordinal));

    /// <summary>
    /// The name of the member at or before line index <paramref name="at"/> (members sit one tab deep in the decompiler's
    /// file-scoped output) and its text up to the next member.
    /// </summary>
    private static (string Name, string Body) EnclosingMethod(string[] lines, int at)
    {
        var start = -1;
        for (var i = at; i >= 0; i--)
        {
            if (IsMethodLine(lines[i]))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            return ("", "");
        }
        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (IsMethodLine(lines[i]))
            {
                end = i;
                break;
            }
        }
        var sig = lines[start];
        var head = sig[..sig.IndexOf('(', StringComparison.Ordinal)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = head.Length > 0 ? head[^1] : "";
        return (name, string.Join('\n', lines[start..end]));
    }
}
