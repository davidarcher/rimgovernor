using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>
/// One vanilla ThoughtDef: how the game grants it and the game state the grant depends on. Owner is hand-kept and never
/// generated.
/// </summary>
/// <param name="Def">The def name.</param>
/// <param name="Kind">"situational" (has a workerClass) or "memory".</param>
/// <param name="Grant">The worker class, or the memory trigger categories and sites.</param>
/// <param name="Dependency">State tokens, ';'-joined and sorted.</param>
/// <param name="Owner">Who owns the def; hand-maintained.</param>
internal sealed record ThoughtRow(string Def, string Kind, string Grant, string Dependency, string Owner);

/// <summary>Classifies defs into table rows and reads and writes the table (Go: <c>BuildRows</c>, <c>Format</c>, <c>ParseOwners</c>).</summary>
internal static partial class ThoughtRows
{
    private const string Header = "def\tkind\tgrant\tdependency\towner";

    private static readonly (string Category, string[] Words)[] TriggerWords =
    [
        ("ingest", ["ingest", "ingested", "eat", "eating", "food", "meal", "nutrition", "ingestible"]),
        ("sleep", ["sleep", "sleeping", "bed", "lay", "wake", "rest", "slept"]),
        ("social", ["social", "interaction", "interactions", "relation", "relations", "romance", "marriage", "opinion", "insult", "chitchat", "lovin", "breakup", "proposal"]),
        ("ritual", ["ritual", "rituals", "gathering", "party", "precept", "precepts", "ideo", "ideology", "outcome", "speech"]),
    ];

    [GeneratedRegex(@"\bRoomStatDefOf\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex StatRoom();

    [GeneratedRegex(@"\bRoomRoleDefOf\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex RoomRole();

    [GeneratedRegex(@"\bneeds\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex NeedField();

    [GeneratedRegex(@"\bNeedDefOf\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex NeedDef();

    [GeneratedRegex(@"\bHediffDefOf\.(\w+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex HediffDef();

    /// <summary>
    /// The game state a decompiled C# body reads, as sorted tokens: room_stat:Stat, room, room_role:Role, need:name,
    /// apparel, temperature, hediff, hediff:Def, light; "other" when none match.
    /// </summary>
    public static IReadOnlyList<string> Dependencies(string src)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in StatRoom().Matches(src))
        {
            set.Add("room_stat:" + m.Groups[1].Value);
        }
        foreach (Match m in RoomRole().Matches(src))
        {
            set.Add("room_role:" + m.Groups[1].Value);
        }
        if (set.Count > 0 || src.Contains("GetRoom(", StringComparison.Ordinal) || src.Contains(".Room", StringComparison.Ordinal))
        {
            set.Add("room");
        }
        foreach (Match m in NeedField().Matches(src))
        {
            if (m.Groups[1].Value is not ("mood" or "AllNeeds" or "TryGetNeed" or "AddOrRemoveNeedsAsAppropriate"))
            {
                set.Add("need:" + m.Groups[1].Value.ToLowerInvariant());
            }
        }
        foreach (Match m in NeedDef().Matches(src))
        {
            set.Add("need:" + m.Groups[1].Value.ToLowerInvariant());
        }
        if (src.Contains("pparel", StringComparison.Ordinal))
        {
            set.Add("apparel");
        }
        if (src.Contains("Temperature", StringComparison.Ordinal))
        {
            set.Add("temperature");
        }
        if (src.Contains("hediffSet", StringComparison.Ordinal) || src.Contains("def.hediff", StringComparison.Ordinal) || HediffDef().IsMatch(src))
        {
            set.Add("hediff");
        }
        foreach (Match m in HediffDef().Matches(src))
        {
            set.Add("hediff:" + m.Groups[1].Value);
        }
        foreach (var k in new[] { "TicksSinceLastLight", "GlowGrid", "GetGlowFor", "PsychGlow", "GlowLevel" })
        {
            if (src.Contains(k, StringComparison.Ordinal))
            {
                set.Add("light");
            }
        }
        return set.Count == 0 ? ["other"] : Sorted(set);
    }

    /// <summary>Classifies a grant site ("Type.Method" or an XML path) as ingest, sleep, social, ritual or other.</summary>
    public static string TriggerCategory(string site)
    {
        var w = Words(site);
        foreach (var (category, words) in TriggerWords)
        {
            if (words.Any(w.Contains))
            {
                return category;
            }
        }
        return "other";
    }

    /// <summary>Classifies every def. <paramref name="xmlFiles"/> lists the non-thought XML files that mention a def name.</summary>
    public static List<ThoughtRow> Build(
        IReadOnlyList<ThoughtDef> defs,
        IReadOnlyDictionary<string, WorkerSource> workers,
        IReadOnlyDictionary<string, List<MemorySite>> sites,
        IReadOnlyDictionary<string, IReadOnlyList<string>> xmlFiles)
    {
        var rows = new List<ThoughtRow>(defs.Count);
        foreach (var d in defs)
        {
            var deps = new HashSet<string>(StringComparer.Ordinal);
            string kind;
            string grant;
            if (d.Worker.Length > 0)
            {
                kind = "situational";
                grant = d.Worker;
                var src = ThoughtSources.WorkerSourceOf(workers, d.Worker);
                if (src is not null)
                {
                    deps.UnionWith(Dependencies(src));
                }
                else
                {
                    deps.Add("other");
                }
                if (d.Hediff.Length > 0)
                {
                    deps.Add("hediff");
                    deps.Add("hediff:" + d.Hediff);
                }
            }
            else
            {
                kind = "memory";
                var cats = new HashSet<string>(StringComparer.Ordinal);
                var grants = new List<string>();
                if (sites.TryGetValue(d.Name, out var defSites))
                {
                    foreach (var s in defSites)
                    {
                        cats.Add(TriggerCategory(s.Site));
                        grants.Add(s.Site);
                        deps.UnionWith(Dependencies(s.Body));
                    }
                }
                if (xmlFiles.TryGetValue(d.Name, out var files))
                {
                    foreach (var f in files)
                    {
                        cats.Add(TriggerCategory(f));
                        grants.Add("xml:" + f);
                    }
                }
                if (cats.Count == 0)
                {
                    cats.Add("other");
                    grants.Add("unreferenced");
                }
                grants.Sort(StringComparer.Ordinal);
                grant = string.Join('+', Sorted(cats)) + " " + string.Join(',', grants);
            }
            if (deps.Count > 1)
            {
                deps.Remove("other");
            }
            if (deps.Count == 0)
            {
                deps.Add("other");
            }
            rows.Add(new ThoughtRow(d.Name, kind, grant, string.Join(';', Sorted(deps)), ""));
        }
        return rows;
    }

    /// <summary>Renders the table, one row per def.</summary>
    public static string Format(IEnumerable<ThoughtRow> rows)
    {
        var b = new StringBuilder(Header).Append('\n');
        foreach (var r in rows)
        {
            b.Append(r.Def).Append('\t').Append(r.Kind).Append('\t').Append(r.Grant).Append('\t').Append(r.Dependency).Append('\t').Append(r.Owner).Append('\n');
        }
        return b.ToString();
    }

    /// <summary>Reads the owner column of an existing table by def name.</summary>
    public static Dictionary<string, string> ParseOwners(string table)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = table.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }
            var f = lines[i].TrimEnd('\r').Split('\t');
            if (f.Length >= 5 && f[4].Length > 0)
            {
                owners[f[0]] = f[4];
            }
        }
        return owners;
    }

    private static List<string> Sorted(IEnumerable<string> set)
    {
        var list = set.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Splits CamelCase, snake_case and dotted names into lower-case words.</summary>
    private static HashSet<string> Words(string s)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        var cur = new StringBuilder();
        void Flush()
        {
            if (cur.Length > 0)
            {
                output.Add(cur.ToString().ToLowerInvariant());
                cur.Clear();
            }
        }
        for (var i = 0; i < s.Length; i++)
        {
            var r = s[i];
            if (!char.IsLetter(r) && !char.IsDigit(r))
            {
                Flush();
            }
            else if (char.IsUpper(r) && cur.Length > 0 && (!char.IsUpper(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
            {
                Flush();
                cur.Append(r);
            }
            else
            {
                cur.Append(r);
            }
        }
        Flush();
        return output;
    }
}
