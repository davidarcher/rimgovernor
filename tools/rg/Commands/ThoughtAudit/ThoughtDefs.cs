using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>
/// One non-abstract ThoughtDef with its workerClass and hediff resolved through ParentName. Fields holds every direct child
/// element by name, the nearest definer in the ParentName chain winning (a list is replaced whole, not merged).
/// </summary>
internal sealed record ThoughtDef(string Name, string Worker, string Hediff, IReadOnlyDictionary<string, XElement> Fields);

/// <summary>Reads the game's ThoughtDef XML.</summary>
internal static partial class ThoughtDefs
{
    private sealed record Raw(string Name, string Parent, string Abstract, string DefName, string Worker, string Hediff, XElement Element);

    [GeneratedRegex(@">\s*([A-Za-z0-9_]+)\s*<", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex XmlToken();

    [GeneratedRegex(@"<defName>[^<]*</defName>", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex DefNameElement();

    /// <summary>
    /// Reads every non-abstract ThoughtDef under <c>dataDir/&lt;pack&gt;/Defs/**.xml</c>, resolving workerClass and hediff
    /// through ParentName. Also returns, per def name, the other def files that mention it as a text token (memory
    /// thoughts granted from XML).
    /// </summary>
    public static (IReadOnlyList<ThoughtDef> Defs, IReadOnlyDictionary<string, IReadOnlyList<string>> XmlFiles) Load(string dataDir)
    {
        var all = new List<Raw>();
        var tokens = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(dataDir))
        {
            throw new ToolException($"{dataDir}: no such directory");
        }
        foreach (var pack in SortedDirectories(dataDir))
        {
            var defs = Path.Combine(pack, "Defs");
            foreach (var path in SortedFiles(defs, ".xml"))
            {
                var data = File.ReadAllText(path, new UTF8Encoding(false));
                if (data.Contains("<ThoughtDef", StringComparison.Ordinal))
                {
                    all.AddRange(Parse(path, data));
                    continue;
                }
                var rel = Path.GetRelativePath(dataDir, path).Replace('\\', '/');
                foreach (Match m in XmlToken().Matches(DefNameElement().Replace(data, "")))
                {
                    var token = m.Groups[1].Value;
                    if (!tokens.TryGetValue(token, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.Ordinal);
                        tokens[token] = set;
                    }
                    set.Add(rel);
                }
            }
        }
        var byName = new Dictionary<string, Raw>(StringComparer.Ordinal);
        foreach (var t in all.Where(t => t.Name.Length > 0))
        {
            byName[t.Name] = t;
        }
        var output = new List<ThoughtDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in all)
        {
            if (string.Equals(t.Abstract, "true", StringComparison.OrdinalIgnoreCase) || t.DefName.Length == 0 || !seen.Add(t.DefName))
            {
                continue;
            }
            output.Add(Resolve(t, byName));
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var files = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (token, set) in tokens)
        {
            if (seen.Contains(token))
            {
                files[token] = [.. set];
            }
        }
        return (output, files);
    }

    /// <summary>Immediate subdirectories in ordinal name order.</summary>
    internal static IEnumerable<string> SortedDirectories(string dir) =>
        Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.Ordinal);

    /// <summary>Files with the extension under dir in <c>filepath.WalkDir</c> order: lexical, depth first.</summary>
    internal static IEnumerable<string> SortedFiles(string dir, string extension)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        var entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(Path.GetFileName, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                foreach (var f in SortedFiles(entry, extension))
                {
                    yield return f;
                }
            }
            else if (entry.EndsWith(extension, StringComparison.Ordinal))
            {
                yield return entry;
            }
        }
    }

    private static ThoughtDef Resolve(Raw t, Dictionary<string, Raw> byName)
    {
        var worker = "";
        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var hediff = "";
        var cur = t;
        for (var depth = 0; depth < 16; depth++)
        {
            if (worker.Length == 0)
            {
                worker = cur.Worker.Trim();
            }
            if (hediff.Length == 0)
            {
                hediff = cur.Hediff.Trim();
            }
            foreach (var child in cur.Element.Elements())
            {
                fields.TryAdd(child.Name.LocalName, child);
            }
            if (cur.Parent.Length == 0 || !byName.TryGetValue(cur.Parent, out var parent))
            {
                break;
            }
            cur = parent;
        }
        return new ThoughtDef(t.DefName, worker, hediff, fields);
    }

    private static List<Raw> Parse(string path, string data)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(data);
        }
        catch (System.Xml.XmlException e)
        {
            throw new ToolException($"{path}: {e.Message}", e);
        }
        var output = new List<Raw>();
        foreach (var e in doc.Root?.Elements("ThoughtDef") ?? [])
        {
            output.Add(new Raw(
                (string?)e.Attribute("Name") ?? "",
                (string?)e.Attribute("ParentName") ?? "",
                (string?)e.Attribute("Abstract") ?? "",
                (string?)e.Element("defName") ?? "",
                (string?)e.Element("workerClass") ?? "",
                (string?)e.Element("hediff") ?? "",
                e));
        }
        return output;
    }
}
