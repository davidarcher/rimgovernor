using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>An assignment to a guard field: the method, the value written, and the guards on that method's path to it. InMethod: the write is in a method, not a constructor, accessor or initializer.</summary>
internal sealed record Writer(string Method, string Hash, string Kind, Expr Value, IReadOnlyList<Guard> Guards, bool ReceiverUnresolved, bool InMethod);

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

    /// <summary>The status of a card whose every site has an unreachable guard (#3243).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The card reason, and primary coverage reason, of an unreachable card.</summary>
    public const string NoWriterOnCallerPath = "no-writer-on-caller-path";

    private readonly SourceIndex index;
    private readonly GuardAnalysis analysis;
    private readonly string root;
    private readonly Dictionary<string, List<SyntaxNode>> callers = new(StringComparer.Ordinal);
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
            var unknown = guards.Count(g => !Exprs.Resolved(g));
            var dead = sites.Count > 0 && sites.All(s => s.Guards.Any(g => g.Unreachable is not null));
            if (dead)
            {
                reasons.Add(NoWriterOnCallerPath);
            }
            var status = dead ? Unreachable
                : sites.Count == 0 || (guards.Count == 0 && reasons.Count > 0) ? "unknown"
                : unknown == 0 && reasons.Count == 0 ? "resolved"
                : unknown == guards.Count && guards.Count > 0 ? "unknown"
                : guards.Count - unknown > 0 ? "partial" : "unknown";
            reasons.AddRange(guards.SelectMany(g => Exprs.Unknowns(g).Select(u => u.Reason)));
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
                Add(callers, name, GuardAnalysis.MemberOf(inv));
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
                var loops = analysis.CountLoops(md, def.Worker);
                foreach (var leaf in Leaves(md))
                {
                    var guards = analysis.GuardsAt(leaf).ToList();
                    guards.AddRange(ReturnValueGuard(md, leaf));
                    sites.Add(MakeSite(md, CountGuards(md, leaf, guards, loops), reasons));
                }
            }
            break;
        }
        if (!found)
        {
            reasons.Add("worker-not-in-sources");
        }
    }

    /// <summary>
    /// Folds a leaf's guards on a counted local (#3281) into one count guard: their conjunction over the count symbol, with
    /// what the loop counts and the leaf's stage (the <c>ActiveAtStage</c> index, else 0) over the count. A leaf before the
    /// loop ends keeps its guards.
    /// </summary>
    private List<Guard> CountGuards(MethodDeclarationSyntax md, ExpressionSyntax leaf, List<Guard> guards, IReadOnlyList<CountLoop> loops)
    {
        foreach (var loop in loops.Where(l => l.End <= leaf.SpanStart))
        {
            Expr? Count(Sym s) => s is { Kind: "local" } && s.Path == loop.Local ? new Sym(loop.Local, "count", "", "int") : null;
            bool Uses(Expr e) => Exprs.Syms(e).Any(s => Count(s) is not null);
            var stage = leaf is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ActiveAtStage" } } inv && inv.ArgumentList.Arguments.Count > 0
                ? analysis.Evaluate(md, inv.ArgumentList.Arguments[0].Expression)
                : new Lit("0");
            var mine = guards.Where(g => g.Count is null && Uses(g.Cond)).ToList();
            if (mine.Count == 0 && !Uses(stage))
            {
                continue;
            }
            var cond = mine.Select(g => Exprs.Replace(g.Cond, Count)).DefaultIfEmpty(new Lit("true")).Aggregate((a, b) => new Op("and", [a, b]));
            var at = mine.Count > 0 ? guards.IndexOf(mine[0]) : guards.Count;
            var count = new Guard(cond, "count", Count: new CountSpec(loop.Over, loop.Where, Exprs.Replace(stage, Count), loop.Via, loop.ViaHash));
            guards = [.. guards.Take(at).Where(g => !mine.Contains(g)), count, .. guards.Skip(at).Where(g => !mine.Contains(g))];
        }
        return guards;
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
        var count = callers.GetValueOrDefault(simple)?.Count ?? 0;
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
        foreach (var sym in guards.SelectMany(g => Exprs.Syms(g)).Where(s => s.Kind is "field" or "prop"))
        {
            if (seen.Add(sym.Decl) && WritersOf(sym.Decl, 0, seen) is { } w)
            {
                fields.Add(w);
            }
        }
        guards = [.. guards.Select(g => UnreachableReason(member, simple, g, fields) is { } why ? g with { Unreachable = why } : g)];
        return new Site(name, file, GuardAnalysis.Hash(member), mods, count, guards, fields);
    }

    /// <summary>
    /// Why the guard can never hold at the site (#3243), or null. Shallow by design: the path is the site's own member,
    /// the members holding its direct call sites (a call in a lambda counts as its enclosing member) and the methods those
    /// members call directly, all matched by simple name. It is claimed only for a guard that is a bool field needing true,
    /// with no initializer, no serialization writer and every writer listed in a method with a resolved receiver, and at
    /// least one such writer (a field no code writes, such as a def field, is set by the XML loader), when the site is a
    /// method with at least one call site and no method-group reference, and no writer is on the path.
    /// </summary>
    private string? UnreachableReason(SyntaxNode member, string simple, Guard guard, IReadOnlyList<FieldWriters> fields)
    {
        if (guard.Cond is not Sym { Kind: "field", Type: "bool" } sym || member is not MethodDeclarationSyntax
            || !callers.TryGetValue(simple, out var sites) || Find(fields, sym.Decl) is not { Kind: "field" } field
            || field.Total != field.Writers.Count || field.Writers.Count == 0
            || field.Writers.Any(w => w.Kind is "init" or "serialization" || w.ReceiverUnresolved || !w.InMethod))
        {
            return null;
        }
        var path = sites.Prepend(member).Distinct().ToList();
        var names = path.Select(GuardAnalysis.NameOf).ToHashSet(StringComparer.Ordinal);
        var callees = path.SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()).Select(CalledName).ToHashSet(StringComparer.Ordinal);
        if (field.Writers.Any(w => names.Contains(w.Method) || callees.Contains(w.Method[(w.Method.LastIndexOf('.') + 1)..])) || MethodGroupReferenced(simple))
        {
            return null;
        }
        var writers = string.Join(", ", field.Writers.Select(w => w.Method).Distinct(StringComparer.Ordinal));
        return $"no writer on the site's direct caller path (writers: {writers}; callers checked: {string.Join(", ", names.Order(StringComparer.Ordinal))}, "
            + "and the methods they call directly); writes in earlier, separate calls on the same instance are not modelled";
    }

    private static FieldWriters? Find(IEnumerable<FieldWriters> fields, string decl) =>
        fields.Select(f => f.Field == decl ? f : Find(f.Following, decl)).FirstOrDefault(f => f is not null);

    private static string CalledName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax a => a.Name.Identifier.Text,
        _ => "",
    };

    /// <summary>
    /// Whether the method name is used anywhere other than in a plain call (<c>Name(...)</c> or <c>x.Name(...)</c>, the forms
    /// counted as call sites) or a method declaration: a delegate, <c>?.</c> or generic call could reach it unseen.
    /// </summary>
    private bool MethodGroupReferenced(string name) => index.Trees.Any(t => t.GetRoot().DescendantTokens()
        .Any(tok => tok.IsKind(SyntaxKind.IdentifierToken) && tok.ValueText == name && tok.Parent is not MethodDeclarationSyntax
            && !(tok.Parent is IdentifierNameSyntax id
                && (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma.Parent : id.Parent) is InvocationExpressionSyntax)));

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
            writers.Add(new Writer(GuardAnalysis.NameOf(m), GuardAnalysis.Hash(m), WriteKind(node), ValueOf(m, node), analysis.GuardsAt(node), unresolved, m is MethodDeclarationSyntax));
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
