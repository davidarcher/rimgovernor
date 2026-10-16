using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>Writes the draft cards (JSON), the shipped card file (JSON) and the all-defs coverage table (TSV) of <c>thoughtaudit -guards</c>; byte-identical for identical input.</summary>
internal static class GuardOutput
{
    private static readonly string[] Scalars =
    [
        "thoughtClass", "workerClass", "hediff", "durationDays", "stackLimit", "stackLimitForSameOtherPawn", "stackedEffectMultiplier",
        "effectMultiplyingStat", "nullifiedIfNotColonist", "minExpectationForNegativeThought", "requiredTraits", "nullifyingTraits",
    ];

    /// <summary>Per-def status plus the dominant reasons, as the summary written at the head of the card file.</summary>
    public static Dictionary<string, object> Summarize(IReadOnlyList<Card> all)
    {
        var byStatus = all.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byKind = all.GroupBy(c => c.Kind + "/" + c.Status).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var primary = all.Where(c => c.Status != "resolved").GroupBy(PrimaryReason).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var guards = all.SelectMany(c => c.Sites).SelectMany(s => s.Guards).ToList();
        var guardReasons = guards.SelectMany(g => Exprs.Unknowns(g.Cond).Select(u => Category(u.Reason))).GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var external = guards.SelectMany(g => Exprs.Unknowns(g.Cond)).Where(u => u.Reason == "external-type").Select(u => u.Text.Split('.')[0])
            .GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(15).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new Dictionary<string, object>
        {
            ["defs"] = all.Count,
            ["status"] = Sorted(byStatus),
            ["kindStatus"] = Sorted(byKind),
            ["primaryReasonOfNonResolved"] = Sorted(primary),
            ["sites"] = all.Sum(c => c.Sites.Count),
            ["guards"] = guards.Count,
            ["guardsResolved"] = guards.Count(g => Exprs.Resolved(g.Cond)),
            ["unknownLeavesByReason"] = Sorted(guardReasons),
            ["unsupportedSyntaxKinds"] = Sorted(guards.SelectMany(g => Exprs.Unknowns(g.Cond)).Where(u => u.Reason.StartsWith("unsupported-syntax:", StringComparison.Ordinal))
                .GroupBy(u => u.Reason[19..]).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
            ["topExternalTypes"] = external,
        };
    }

    /// <summary>The reason that explains a non-resolved def: the most frequent unknown-leaf reason, ties broken ordinally.</summary>
    public static string PrimaryReason(Card c)
    {
        var leaves = c.Sites.SelectMany(s => s.Guards).SelectMany(g => Exprs.Unknowns(g.Cond)).Select(u => Category(u.Reason)).ToList();
        var structural = c.Reasons.Where(r => r is "xml-granted" or "no-code-or-xml-reference" or "worker-not-in-sources" or "conditions-in-callers" or "event-driven").ToList();
        if (structural.Count > 0 && (leaves.Count == 0 || c.Sites.Count == 0))
        {
            return structural[0];
        }
        return leaves.Count == 0 ? "none" : leaves.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    private static string Category(string reason) => reason.Split(':')[0];

    private static SortedDictionary<string, int> Sorted(Dictionary<string, int> d) => new(d, StringComparer.Ordinal);

    public static string Coverage(IReadOnlyList<Card> all)
    {
        var sb = new StringBuilder("def\tkind\towner\tstatus\tsites\tguards\tunknown\tprimary_reason\n");
        foreach (var c in all)
        {
            var guards = c.Sites.SelectMany(s => s.Guards).ToList();
            sb.Append(c.Def).Append('\t').Append(c.Kind).Append('\t').Append(c.Owner).Append('\t').Append(c.Status).Append('\t')
                .Append(c.Sites.Count).Append('\t').Append(guards.Count).Append('\t').Append(guards.Count(g => !Exprs.Resolved(g.Cond))).Append('\t')
                .Append(c.Status == "resolved" ? "" : PrimaryReason(c)).Append('\n');
        }
        return sb.ToString();
    }

    public static string Cards(string game, IReadOnlyDictionary<string, object> summary, IReadOnlyList<Card> cards)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("generator", "RimGovernor.cmd thoughtaudit -guards");
            w.WriteString("status", "draft: extracted from syntax only, no probe has verified any card");
            w.WriteString("game", game);
            w.WritePropertyName("coverage");
            WriteAny(w, summary);
            w.WriteStartArray("cards");
            foreach (var c in cards)
            {
                WriteCard(w, c);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>The schema of the shipped card file; <c>MechanicCards</c> in Brain.Policy rejects any other.</summary>
    public const int ShippedSchema = 1;

    private static readonly string[] ShippedNumbers = ["durationDays", "stackLimit", "stackedEffectMultiplier"];

    /// <summary>
    /// The shipped card file (<c>brain/src/RimGovernor.Brain.Policy/mechanic_cards.json</c>, #3220): the given cards with typed
    /// XML numbers and only the fields the brain's records read. A card is <c>verified</c> only when its def is in
    /// <paramref name="verified"/> (its drift hash matches a row of the verified-cards table, #3222), else <c>extracted</c>.
    /// </summary>
    public static string Shipped(string game, IReadOnlyList<Card> cards, IReadOnlySet<string> verified)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", ShippedSchema);
            w.WriteString("game", game);
            w.WriteStartArray("cards");
            foreach (var c in cards)
            {
                w.WriteStartObject();
                w.WriteString("def", c.Def);
                w.WriteString("kind", c.Kind);
                w.WriteString("status", verified.Contains(c.Def) ? "verified" : "extracted");
                w.WriteStartObject("xml");
                foreach (var name in ShippedNumbers.Where(c.Data.Fields.ContainsKey))
                {
                    w.WriteNumber(name, double.Parse(c.Data.Fields[name].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture));
                }
                w.WriteStartArray("nullifyingTraits");
                foreach (var li in c.Data.Fields.TryGetValue("nullifyingTraits", out var traits) ? traits.Elements("li") : [])
                {
                    w.WriteStringValue(li.Value.Trim());
                }
                w.WriteEndArray();
                w.WriteStartArray("stages");
                foreach (var li in c.Data.Fields.TryGetValue("stages", out var stages) ? stages.Elements("li") : [])
                {
                    w.WriteStartObject();
                    if (li.Element("baseMoodEffect") is { } e)
                    {
                        w.WriteNumber("baseMoodEffect", double.Parse(e.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture));
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartArray("sites");
                foreach (var s in c.Sites)
                {
                    WriteSite(w, s, shipped: true);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>The body hashes a card's drift hash covers: each grant site and each writer, follows included.</summary>
    public static IEnumerable<string> MemberHashes(Card c)
    {
        static IEnumerable<string> Of(FieldWriters f) => f.Writers.Select(x => x.Hash).Concat(f.Following.SelectMany(Of));
        return c.Sites.SelectMany(s => s.Writers.SelectMany(Of).Prepend(s.Hash));
    }

    private static void WriteAny(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case int i:
                w.WriteNumberValue(i);
                break;
            case IReadOnlyDictionary<string, int> d:
                w.WriteStartObject();
                foreach (var (k, v) in d.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WriteNumber(k, v);
                }
                w.WriteEndObject();
                break;
            case IReadOnlyDictionary<string, object> d:
                w.WriteStartObject();
                foreach (var (k, v) in d)
                {
                    w.WritePropertyName(k);
                    WriteAny(w, v);
                }
                w.WriteEndObject();
                break;
            default:
                w.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteCard(Utf8JsonWriter w, Card c)
    {
        w.WriteStartObject();
        w.WriteString("def", c.Def);
        w.WriteString("kind", c.Kind);
        if (c.Owner.Length > 0)
        {
            w.WriteString("owner", c.Owner);
        }
        w.WriteString("status", c.Status);
        w.WriteStartArray("reasons");
        foreach (var r in c.Reasons)
        {
            w.WriteStringValue(r);
        }
        w.WriteEndArray();
        w.WriteStartObject("xml");
        foreach (var name in Scalars.Where(c.Data.Fields.ContainsKey))
        {
            w.WriteString(name, string.Join(' ', c.Data.Fields[name].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        w.WriteStartArray("stages");
        foreach (var li in c.Data.Fields.TryGetValue("stages", out var stages) ? stages.Elements("li") : [])
        {
            w.WriteStartObject();
            foreach (var f in new[] { "label", "baseMoodEffect", "baseOpinionOffset" })
            {
                if (li.Element(f) is { } e)
                {
                    w.WriteString(f, e.Value.Trim());
                }
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteStartArray("sites");
        foreach (var s in c.Sites)
        {
            WriteSite(w, s, shipped: false);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>A site; <paramref name="shipped"/> leaves out the draft-only fields (file, guard text and resolution, writer totals, derived inputs).</summary>
    private static void WriteSite(Utf8JsonWriter w, Site s, bool shipped)
    {
        w.WriteStartObject();
        w.WriteString("site", s.Name);
        if (!shipped)
        {
            w.WriteString("file", s.File);
        }
        w.WriteString("bodyHash", s.Hash);
        w.WriteString("access", s.Access);
        w.WriteNumber("callers", s.Callers);
        w.WriteStartArray("guards");
        foreach (var g in s.Guards)
        {
            WriteGuard(w, g, shipped);
        }
        w.WriteEndArray();
        w.WriteStartArray("writers");
        foreach (var f in s.Writers)
        {
            WriteFieldWriters(w, f, shipped);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteGuard(Utf8JsonWriter w, Guard g, bool shipped)
    {
        w.WriteStartObject();
        w.WriteString("origin", g.Origin);
        if (!shipped)
        {
            w.WriteString("text", Exprs.Render(g.Cond));
            w.WriteString("conditions", Exprs.Resolved(g.Cond) ? "resolved" : "unknown");
        }
        w.WritePropertyName("expr");
        WriteExpr(w, g.Cond);
        w.WriteEndObject();
    }

    private static void WriteFieldWriters(Utf8JsonWriter w, FieldWriters f, bool shipped)
    {
        w.WriteStartObject();
        w.WriteString("field", f.Field);
        w.WriteString("kind", f.Kind);
        w.WriteNumber("writersFound", f.Total);
        w.WriteNumber("resetWriters", f.Writers.Count(x => x.Kind == "reset"));
        if (!shipped && f.Derived.Count > 0)
        {
            w.WriteStartArray("derivedFrom");
            foreach (var d in f.Derived)
            {
                w.WriteStringValue(d);
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("writers");
        foreach (var x in f.Writers)
        {
            w.WriteStartObject();
            w.WriteString("method", x.Method);
            w.WriteString("bodyHash", x.Hash);
            w.WriteString("write", x.Kind);
            w.WriteString("value", Exprs.Render(x.Value));
            if (x.ReceiverUnresolved)
            {
                w.WriteBoolean("receiverUnresolved", true);
            }
            w.WriteStartArray("guards");
            foreach (var g in x.Guards)
            {
                WriteGuard(w, g, shipped);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("follows");
        foreach (var n in f.Following)
        {
            WriteFieldWriters(w, n, shipped);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteExpr(Utf8JsonWriter w, Expr e)
    {
        w.WriteStartObject();
        switch (e)
        {
            case Sym s:
                w.WriteString("sym", s.Path);
                w.WriteString("kind", s.Kind);
                w.WriteString("decl", s.Decl);
                if (s.Type is not null)
                {
                    w.WriteString("type", s.Type);
                }
                break;
            case Lit l:
                w.WriteString("lit", l.Text);
                break;
            case Unknown u:
                w.WriteString("unknown", u.Reason);
                w.WriteString("text", u.Text);
                break;
            case Op o:
                w.WriteString("op", o.Name);
                w.WriteStartArray("args");
                foreach (var a in o.Args)
                {
                    WriteExpr(w, a);
                }
                w.WriteEndArray();
                break;
            default:
                break;
        }
        w.WriteEndObject();
    }
}
