using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>An assignment to a guard field: the method, the value written, and the guards on that method's path to it.</summary>
internal sealed record Writer(string Method, string Hash, string Kind, Expr Value, IReadOnlyList<Guard> Guards, bool ReceiverUnresolved);

/// <summary>A guard field with every writer found in the sources; Following holds the fields its writers copy from; Derived a computed property's inputs.</summary>
internal sealed record FieldWriters(string Field, string Kind, int Total, IReadOnlyList<Writer> Writers, IReadOnlyList<FieldWriters> Following, IReadOnlyList<string> Derived);

/// <summary>A place that grants a thought, with the guards on its path inside the granting member.</summary>
internal sealed record Site(string Name, string File, string Hash, string Access, int Callers, IReadOnlyList<Guard> Guards, IReadOnlyList<FieldWriters> Writers);

/// <summary>A draft mechanic card for one ThoughtDef: XML data, grant sites with guards and writers, and the coverage status.</summary>
internal sealed record Card(string Def, string Kind, string Owner, ThoughtDef Data, string Status, IReadOnlyList<Site> Sites, IReadOnlyList<string> Reasons);

/// <summary>Builds draft cards over the decompiled sources (#3218). Deterministic: everything is sorted ordinally.</summary>
internal sealed class GuardCards
{
    private const int MaxWriters = 25;

    private readonly SourceIndex index;
    private readonly GuardAnalysis analysis;
    private readonly string root;
    private readonly Dictionary<string, int> callers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SyntaxNode>> writes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SyntaxNode>> grants = new(StringComparer.Ordinal);

    public GuardCards(SourceIndex index, string root, IReadOnlySet<string> defNames)
    {
        this.index = index;
        this.root = root;
        analysis = new GuardAnalysis(index);
        foreach (var tree in index.Trees)
        {
            foreach (var n in tree.GetRoot().DescendantNodes())
            {
                Scan(n, defNames);
            }
        }
    }

    public IReadOnlyList<Card> Build(IReadOnlyList<ThoughtDef> defs, IReadOnlyDictionary<string, string> owners, IReadOnlyDictionary<string, IReadOnlyList<string>> xmlFiles)
    {
        var cards = new List<Card>();
        foreach (var def in defs)
        {
            var sites = new List<Site>();
            var reasons = new List<string>();
            if (def.Worker.Length > 0)
            {
                SituationalSites(def, sites, reasons);
            }
            else
            {
                MemorySites(def, xmlFiles, sites, reasons);
            }
            var guards = sites.SelectMany(s => s.Guards).ToList();
            var unknown = guards.Count(g => !Exprs.Resolved(g.Cond));
            var status = sites.Count == 0 || (guards.Count == 0 && reasons.Count > 0) ? "unknown"
                : unknown == 0 && reasons.Count == 0 ? "resolved"
                : unknown == guards.Count && guards.Count > 0 ? "unknown"
                : guards.Count - unknown > 0 ? "partial" : "unknown";
            reasons.AddRange(guards.SelectMany(g => Exprs.Unknowns(g.Cond).Select(u => u.Reason)));
            cards.Add(new Card(def.Name, def.Worker.Length > 0 ? "situational" : "memory", owners.GetValueOrDefault(def.Name, ""), def, status, sites,
                [.. reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        }
        return cards;
    }

    private void Scan(SyntaxNode n, IReadOnlySet<string> defNames)
    {
        switch (n)
        {
            case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "ThoughtDefOf" } } m when defNames.Contains(m.Name.Identifier.Text):
                AddGrant(m.Name.Identifier.Text, m);
                break;
            case InvocationExpressionSyntax inv:
                var name = inv.Expression switch
                {
                    IdentifierNameSyntax i => i.Identifier.Text,
                    MemberAccessExpressionSyntax a => a.Name.Identifier.Text,
                    _ => "",
                };
                if (name.Length > 0)
                {
                    callers[name] = callers.GetValueOrDefault(name) + 1;
                }
                if (name is "GetNamed" or "GetNamedSilentFail" or "Named" && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit
                    && lit.IsKind(SyntaxKind.StringLiteralExpression) && defNames.Contains(lit.Token.ValueText))
                {
                    AddGrant(lit.Token.ValueText, inv);
                }
                break;
            case AssignmentExpressionSyntax a:
                Add(writes, Tail(a.Left), a);
                break;
            case PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression):
                Add(writes, Tail(p.Operand), p);
                break;
            case PostfixUnaryExpressionSyntax q:
                Add(writes, Tail(q.Operand), q);
                break;
            case ArgumentSyntax { RefKindKeyword.Text: "ref" or "out" } arg:
                Add(writes, Tail(arg.Expression), arg);
                break;
            case VariableDeclaratorSyntax { Initializer: not null, Parent.Parent: FieldDeclarationSyntax } v:
                Add(writes, v.Identifier.Text, v);
                break;
            default:
                break;
        }
    }

    /// <summary>Records a reference to a thought def unless it only removes, counts, reads or compares the memory (those never grant it).</summary>
    private void AddGrant(string def, SyntaxNode reference)
    {
        var call = reference.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        var name = call?.Expression switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            MemberAccessExpressionSyntax a => a.Name.Identifier.Text,
            _ => "",
        };
        var reads = name.StartsWith("Remove", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal)
            || name.StartsWith("Num", StringComparison.Ordinal) || name is "Contains" or "Has";
        if (!reads && reference.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression })
        {
            Add(grants, def, reference);
        }
    }

    private static void Add(Dictionary<string, List<SyntaxNode>> map, string key, SyntaxNode node)
    {
        if (key.Length == 0)
        {
            return;
        }
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(node);
    }

    private static string Tail(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        ParenthesizedExpressionSyntax p => Tail(p.Expression),
        _ => "",
    };

    private void MemorySites(ThoughtDef def, IReadOnlyDictionary<string, IReadOnlyList<string>> xmlFiles, List<Site> sites, List<string> reasons)
    {
        if (!grants.TryGetValue(def.Name, out var nodes))
        {
            reasons.Add(xmlFiles.ContainsKey(def.Name) ? "xml-granted" : "no-code-or-xml-reference");
            return;
        }
        var byMember = nodes.GroupBy(GuardAnalysis.MemberOf).ToList();
        foreach (var g in byMember)
        {
            var guards = analysis.GuardsAt(g.First());
            sites.Add(MakeSite(g.Key, guards, reasons));
        }
        sites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    private void SituationalSites(ThoughtDef def, List<Site> sites, List<string> reasons)
    {
        var found = false;
        foreach (var type in index.Chain(def.Worker).Where(index.HasType))
        {
            var methods = index.MembersOf(type).Where(m => m.Node is MethodDeclarationSyntax && m.Name is "CurrentStateInternal" or "CurrentSocialStateInternal").ToList();
            if (methods.Count == 0)
            {
                continue;
            }
            found = true;
            foreach (var m in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var md = (MethodDeclarationSyntax)m.Node;
                foreach (var leaf in Leaves(md))
                {
                    var guards = analysis.GuardsAt(leaf).ToList();
                    guards.AddRange(ReturnValueGuard(md, leaf));
                    sites.Add(MakeSite(md, guards, reasons));
                }
            }
            break;
        }
        if (!found)
        {
            reasons.Add("worker-not-in-sources");
        }
    }

    private static IEnumerable<ExpressionSyntax> Leaves(MethodDeclarationSyntax md)
    {
        var roots = md.ExpressionBody is { } arrow
            ? [arrow.Expression]
            : md.Body?.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>().ToList() ?? [];
        return roots.SelectMany(Expand);
    }

    private static IEnumerable<ExpressionSyntax> Expand(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax p => Expand(p.Expression),
        ConditionalExpressionSyntax c => Expand(c.WhenTrue).Concat(Expand(c.WhenFalse)),
        MemberAccessExpressionSyntax { Name.Identifier.Text: "Inactive" } => [],
        _ => [e],
    };

    private IEnumerable<Guard> ReturnValueGuard(SyntaxNode member, ExpressionSyntax leaf)
    {
        if (leaf is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ActiveAtStage" or "ActiveDefault" } }
            or MemberAccessExpressionSyntax { Name.Identifier.Text: "ActiveDefault" })
        {
            return [];
        }
        var e = analysis.Evaluate(member, leaf);
        if (e is Lit { Text: "true" })
        {
            return [];
        }
        var boolean = e is Op { Name: "and" or "or" or "not" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "is" } || analysis.TypeOf(member, leaf) == "bool";
        return [new Guard(boolean || e is Unknown ? e : new Unknown("state-expression", Exprs.Render(e)), "return-value")];
    }

    private Site MakeSite(SyntaxNode member, IReadOnlyList<Guard> guards, List<string> reasons)
    {
        var name = GuardAnalysis.NameOf(member);
        var simple = name[(name.LastIndexOf('.') + 1)..];
        var count = callers.GetValueOrDefault(simple);
        if (guards.Count == 0)
        {
            reasons.Add(count > 0 ? "conditions-in-callers" : "event-driven");
        }
        var mods = member switch
        {
            BaseMethodDeclarationSyntax b => b.Modifiers.ToString(),
            PropertyDeclarationSyntax p => p.Modifiers.ToString(),
            _ => "",
        };
        var file = Path.GetRelativePath(root, member.SyntaxTree.FilePath).Replace('\\', '/');
        var fields = new List<FieldWriters>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sym in guards.SelectMany(g => Exprs.Syms(g.Cond)).Where(s => s.Kind is "field" or "prop"))
        {
            if (seen.Add(sym.Decl) && WritersOf(sym.Decl, 0, seen) is { } w)
            {
                fields.Add(w);
            }
        }
        return new Site(name, file, GuardAnalysis.Hash(member), mods, count, guards, fields);
    }

    private FieldWriters? WritersOf(string decl, int depth, HashSet<string> seen)
    {
        var dot = decl.IndexOf('.', StringComparison.Ordinal);
        var owner = decl[..dot];
        var name = decl[(dot + 1)..];
        var member = index.Find(owner, name, out _).FirstOrDefault(m => m.Kind is "field" or "prop");
        if (member is null)
        {
            return null;
        }
        if (member.Node is PropertyDeclarationSyntax prop && (prop.ExpressionBody is not null || prop.AccessorList?.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) == true))
        {
            var body = prop.ExpressionBody?.Expression ?? prop.AccessorList!.Accessors.First(a => a.Keyword.IsKind(SyntaxKind.GetKeyword)).ExpressionBody?.Expression;
            var inputs = body is null ? ["multi-statement getter"] : Exprs.Syms(analysis.Evaluate(prop, body)).Select(s => s.Decl).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return new FieldWriters(decl, "derived", 0, [], [], inputs);
        }
        var writers = new List<Writer>();
        var total = 0;
        foreach (var node in writes.GetValueOrDefault(name) ?? [])
        {
            var (applies, unresolved) = Applies(node, decl);
            if (!applies)
            {
                continue;
            }
            total++;
            var m = GuardAnalysis.MemberOf(node);
            writers.Add(new Writer(GuardAnalysis.NameOf(m), GuardAnalysis.Hash(m), WriteKind(node), ValueOf(m, node), analysis.GuardsAt(node), unresolved));
        }
        writers = [.. writers.OrderBy(w => w.Method, StringComparer.Ordinal).ThenBy(w => w.Kind, StringComparer.Ordinal).Take(MaxWriters)];
        var following = new List<FieldWriters>();
        if (depth < 1)
        {
            foreach (var sym in writers.SelectMany(w => Exprs.Syms(w.Value)).Where(s => s.Kind is "field" or "prop" && s.Decl != decl).OrderBy(s => s.Decl, StringComparer.Ordinal))
            {
                if (seen.Add(sym.Decl) && WritersOf(sym.Decl, depth + 1, seen) is { } w)
                {
                    following.Add(w);
                }
            }
        }
        return new FieldWriters(decl, member.Kind, total, writers, following, []);
    }

    private (bool Applies, bool Unresolved) Applies(SyntaxNode node, string decl)
    {
        var m = GuardAnalysis.MemberOf(node);
        if (node is VariableDeclaratorSyntax v)
        {
            return (SourceIndex.OwnerOf(v) + "." + v.Identifier.Text == decl, false);
        }
        var target = node switch
        {
            AssignmentExpressionSyntax a => a.Left,
            PrefixUnaryExpressionSyntax p => p.Operand,
            PostfixUnaryExpressionSyntax q => q.Operand,
            ArgumentSyntax g => g.Expression,
            _ => null,
        };
        if (target is null)
        {
            return (false, false);
        }
        var e = analysis.Evaluate(m, target);
        if (e is Sym s)
        {
            return (s.Decl == decl && s.Kind is "field" or "prop", false);
        }
        return (e is Unknown, e is Unknown);
    }

    private static string WriteKind(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax => "init",
        ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: var t } } } } when t.StartsWith("Scribe", StringComparison.Ordinal) => "serialization",
        ArgumentSyntax => "ref-arg",
        PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => "step",
        AssignmentExpressionSyntax { Right: LiteralExpressionSyntax { Token.Text: "false" or "0" or "0f" or "null" } } => "reset",
        AssignmentExpressionSyntax { Right: DefaultExpressionSyntax } => "reset",
        _ => "set",
    };

    private Expr ValueOf(SyntaxNode member, SyntaxNode node) => node switch
    {
        AssignmentExpressionSyntax a => analysis.Evaluate(member, a.Right),
        VariableDeclaratorSyntax { Initializer.Value: { } init } => analysis.Evaluate(member, init),
        ArgumentSyntax g => new Lit(g.RefKindKeyword.Text),
        _ => new Lit("step"),
    };
}
