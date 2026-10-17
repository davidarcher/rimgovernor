using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>A condition as a tree over named symbols (#3218). Unknown marks everything the syntax cannot resolve; it is never guessed.</summary>
internal abstract record Expr;

/// <summary>A resolved symbol path: Kind is param, local, field, prop, method, enum or type; Decl is <c>Owner.Member</c> of the last segment.</summary>
internal sealed record Sym(string Path, string Kind, string Decl, string? Type) : Expr;

internal sealed record Lit(string Text) : Expr;

internal sealed record Op(string Name, IReadOnlyList<Expr> Args) : Expr;

/// <summary>An unresolved piece: a stable reason category (unresolved-symbol, external-type, virtual-call, ...) and the source text.</summary>
internal sealed record Unknown(string Reason, string Text) : Expr;

/// <summary>
/// One condition on the path to a node, with the polarity already applied and the syntax it came from. Unreachable is set
/// (#3243) when no writer on the site's direct caller path can make the guard hold; its text says what was checked. A
/// count guard (#3281, origin <c>count</c>) carries <see cref="Count"/>; its Cond compares the count symbol (kind
/// <c>count</c>) with literals.
/// </summary>
internal sealed record Guard(Expr Cond, string Origin, string? Unreachable = null, CountSpec? Count = null);

/// <summary>
/// What a count guard counts (#3281): the elements of <see cref="Over"/> for which <see cref="Where"/> holds, and the stage
/// index the site returns as an expression over the count. <see cref="Via"/> and <see cref="ViaHash"/> name the worker
/// override the predicate was resolved through, when it was a virtual call.
/// </summary>
internal sealed record CountSpec(Expr Over, Expr Where, Expr Stage, string? Via = null, string? ViaHash = null);

/// <summary>A local counted by a loop (#3281): <c>foreach (x in Over) if (Where(x)) Local++;</c> or the indexed-for form, ending at <see cref="End"/>.</summary>
internal sealed record CountLoop(string Local, int End, Expr Over, Expr Where, string? Via, string? ViaHash);

/// <summary>
/// The stage a site returns through <c>ActiveAtStage(local)</c> (#3324): the stage is the first threshold whose
/// <c>Below</c> the <see cref="Input"/> is under, else <see cref="Else"/>, plus every modifier delta whose When holds.
/// </summary>
internal sealed record StageBand(Expr Input, IReadOnlyList<(double Below, int Stage)> Thresholds, int Else, IReadOnlyList<(Expr When, double Delta)> Modifiers);

/// <summary>Renders, and inspects, expression trees.</summary>
internal static class Exprs
{
    /// <summary>A guard's expressions: its condition and, for a count guard, what it counts and its stage.</summary>
    public static IEnumerable<Expr> Parts(Guard g) => g.Count is { } c ? [g.Cond, c.Over, c.Where, c.Stage] : [g.Cond];

    public static string Render(Guard g) => g.Count is { } c
        ? Render(g.Cond) + " [count " + Render(c.Over) + " where " + Render(c.Where) + "; stage " + Render(c.Stage) + "]"
        : Render(g.Cond);

    public static IEnumerable<Unknown> Unknowns(Guard g) => Parts(g).SelectMany(Unknowns);

    public static IEnumerable<Sym> Syms(Guard g) => Parts(g).SelectMany(Syms);

    public static bool Resolved(Guard g) => !Unknowns(g).Any();

    /// <summary>The tree with every symbol <paramref name="map"/> returns a replacement for replaced.</summary>
    public static Expr Replace(Expr e, Func<Sym, Expr?> map) => e switch
    {
        Sym s => map(s) ?? s,
        Op o => o with { Args = [.. o.Args.Select(a => Replace(a, map))] },
        _ => e,
    };

    public static string Render(Expr e) => e switch
    {
        Sym s => s.Path,
        Lit l => l.Text,
        Unknown u => "?" + u.Reason,
        Op { Name: "not" } o => "!" + Render(o.Args[0]),
        Op { Name: "call" } o => Render(o.Args[0]) + "(" + string.Join(", ", o.Args.Skip(1).Select(Render)) + ")",
        Op { Args.Count: 2 } o => "(" + Render(o.Args[0]) + " " + o.Name + " " + Render(o.Args[1]) + ")",
        Op o => o.Name + "(" + string.Join(", ", o.Args.Select(Render)) + ")",
        _ => "",
    };

    public static IEnumerable<Unknown> Unknowns(Expr e) => e switch
    {
        Unknown u => [u],
        Op o => o.Args.SelectMany(Unknowns),
        _ => [],
    };

    public static IEnumerable<Sym> Syms(Expr e) => e switch
    {
        Sym s => [s],
        Op o => o.Args.SelectMany(Syms),
        _ => [],
    };

    public static bool Resolved(Expr e) => !Unknowns(e).Any();

    /// <summary>The unknown reason of a guard leaf over a local whose value <see cref="GuardAnalysis"/> could not trace (#3324).</summary>
    public const string UntracedLocal = "untraced-local";

    /// <summary>The expression with every remaining local symbol (one the analysis left untraced) an unknown <see cref="UntracedLocal"/>.</summary>
    public static Expr Untraced(Expr e) => Replace(e, s => s.Kind == "local" ? new Unknown(UntracedLocal, s.Path) : null);

    /// <summary>The guard with <see cref="Untraced(Expr)"/> applied to its condition and to what a count guard counts.</summary>
    public static Guard Untraced(Guard g) => g with
    {
        Cond = Untraced(g.Cond),
        Count = g.Count is { } c ? c with { Over = Untraced(c.Over), Where = Untraced(c.Where), Stage = Untraced(c.Stage) } : null,
    };
}

/// <summary>
/// Syntax-only guard extraction (#3218): the conditions between a member's entry and a node (enclosing if/else, ternary and
/// short-circuit operands, early-exit guards, loop headers), each as an <see cref="Expr"/>. Types come from the declared
/// type text in <see cref="SourceIndex"/>; virtual dispatch, constructions, indexers, lambdas, switches and anything the
/// sources do not declare become <see cref="Unknown"/> with a reason.
/// </summary>
internal sealed class GuardAnalysis(SourceIndex index)
{
    private sealed record Ty(string Name, bool IsStatic);

    private sealed record Res(Expr E, Ty? T);

    /// <summary>The loop element while a count predicate is evaluated: the foreach variable or the indexed <c>C[i]</c>, by its text.</summary>
    private sealed record Element(string Text, Res Res);

    private const int MaxDepth = 8;

    private Element? element;

    /// <summary>The guards on the path from the node's member entry (or from below <paramref name="stop"/>, an ancestor) to the node, outermost first.</summary>
    public IReadOnlyList<Guard> GuardsAt(SyntaxNode node, SyntaxNode? stop = null)
    {
        var member = MemberOf(node);
        var levels = new List<List<Guard>>();
        var cur = node;
        while (cur.Parent is { } parent && cur != member && parent != stop)
        {
            var level = new List<Guard>();
            switch (parent)
            {
                case IfStatementSyntax i when cur == i.Statement:
                    Flatten(level, member, i.Condition, positive: true, "if");
                    break;
                case ElseClauseSyntax { Parent: IfStatementSyntax i }:
                    Flatten(level, member, i.Condition, positive: false, "else");
                    break;
                case ConditionalExpressionSyntax c when cur == c.WhenTrue:
                    Flatten(level, member, c.Condition, positive: true, "ternary");
                    break;
                case ConditionalExpressionSyntax c when cur == c.WhenFalse:
                    Flatten(level, member, c.Condition, positive: false, "ternary");
                    break;
                case BinaryExpressionSyntax b when cur == b.Right && b.IsKind(SyntaxKind.LogicalAndExpression):
                    Flatten(level, member, b.Left, positive: true, "and");
                    break;
                case BinaryExpressionSyntax b when cur == b.Right && b.IsKind(SyntaxKind.LogicalOrExpression):
                    Flatten(level, member, b.Left, positive: false, "or");
                    break;
                case WhileStatementSyntax w when cur == w.Statement:
                    Flatten(level, member, w.Condition, positive: true, "while");
                    break;
                case ForStatementSyntax { Condition: { } cond } f when cur == f.Statement:
                    Flatten(level, member, cond, positive: true, "for");
                    break;
                case CommonForEachStatementSyntax f when cur == f.Statement:
                    level.Add(new Guard(new Op("nonempty", [Eval(member, f.Expression, f.SpanStart, 0).E]), "foreach"));
                    break;
                case SwitchSectionSyntax or SwitchExpressionArmSyntax:
                    level.Add(new Guard(new Unknown("switch", "case label"), "switch"));
                    break;
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    level.Add(new Guard(new Lit("deferred: runs when the enclosing delegate is invoked"), "lambda"));
                    break;
                case BlockSyntax block:
                    foreach (var s in block.Statements.TakeWhile(s => s != cur))
                    {
                        if (s is IfStatementSyntax { Else: null } early && Exits(early.Statement))
                        {
                            Flatten(level, member, early.Condition, positive: false, "early-exit");
                        }
                    }
                    break;
                default:
                    break;
            }
            levels.Add(level);
            cur = parent;
        }
        levels.Reverse();
        return [.. levels.SelectMany(l => l)];
    }

    /// <summary>The member (method, constructor, accessor, expression-bodied property) enclosing the node.</summary>
    public static SyntaxNode MemberOf(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax)
        ?? node.SyntaxTree.GetRoot();

    /// <summary>The member's qualified name for display: <c>Type.Member</c>.</summary>
    public static string NameOf(SyntaxNode member)
    {
        var owner = SourceIndex.OwnerOf(member);
        return owner + "." + member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax p } a => p.Identifier.Text + "." + a.Keyword.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables[0].Identifier.Text,
            _ => "?",
        };
    }

    /// <summary>A stable hash of a member's normalized syntax, so a game update that changes the method is detectable.</summary>
    public static string Hash(SyntaxNode member) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member.NormalizeWhitespace("  ", "\n").ToFullString())))[..16].ToLowerInvariant();

    /// <summary>Evaluates an expression written at <paramref name="at"/> inside its member.</summary>
    public Expr Evaluate(SyntaxNode member, ExpressionSyntax e) => Eval(member, e, e.SpanStart, 0).E;

    /// <summary>The static type name of an expression, when the sources determine it.</summary>
    public string? TypeOf(SyntaxNode member, ExpressionSyntax e) => Eval(member, e, e.SpanStart, 0).T?.Name;

    /// <summary>
    /// The locals of <paramref name="md"/> counted by a loop (#3281), in source order. The pattern is generic: a local
    /// initialised to 0 whose only write is one increment statement, reached from the body of a top-level
    /// <c>foreach (x in C)</c> or <c>for (int i = 0; i &lt; C.Count; i++)</c> through if/else nesting only, with no jump
    /// before it in an enclosing block and no break, return, goto, throw or yield in the loop. The predicate is the
    /// conjunction of those if conditions over the element. A virtual call on this whose one argument is the element is
    /// resolved through <paramref name="worker"/>'s override when that override returns a single expression; otherwise it
    /// is an unknown virtual-call, never guessed; a local in the predicate is an unknown loop-local.
    /// </summary>
    public IReadOnlyList<CountLoop> CountLoops(MethodDeclarationSyntax md, string? worker)
    {
        var result = new List<CountLoop>();
        if (md.Body is not { } body)
        {
            return result;
        }
        foreach (var v in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var name = v.Identifier.Text;
            var writes = body.DescendantNodes().Where(n => Writes(n, name)).ToList();
            if (v.Initializer?.Value is not LiteralExpressionSyntax { Token.Text: "0" } || writes.Count != 1
                || writes[0] is not ExpressionSyntax { Parent: ExpressionStatementSyntax step } inc || !Increments(inc)
                || Enclosing(step) is not { } found || found.Loop.Parent != body
                || found.Statement.DescendantNodesAndSelf().Any(n => n is BreakStatementSyntax or ReturnStatementSyntax or GotoStatementSyntax or ThrowStatementSyntax or YieldStatementSyntax))
            {
                continue;
            }
            ExpressionSyntax collection;
            string text;
            string? type;
            switch (found.Loop)
            {
                case ForEachStatementSyntax f:
                    (collection, text) = (f.Expression, f.Identifier.Text);
                    type = f.Type is { IsVar: false } t ? SourceIndex.TypeName(t) : ElementType(md, f.Expression, f.SpanStart);
                    break;
                case ForStatementSyntax f when IndexedOver(f) is { } over:
                    (collection, text) = (over.Collection, Squash(over.Collection) + "[" + over.Index + "]");
                    type = ElementType(md, over.Collection, f.SpanStart);
                    break;
                default:
                    continue;
            }
            var of = Over(md, collection, found.Loop.SpanStart);
            element = new Element(text, new Res(new Sym(text, "element", "", type), type is null ? null : new Ty(type, false)));
            try
            {
                string? via = null, viaHash = null;
                Expr? where = null;
                foreach (var (cond, positive) in found.Conds)
                {
                    var p = Predicate(md, cond, worker, ref via, ref viaHash);
                    p = positive ? p : new Op("not", [p]);
                    where = where is null ? p : new Op("and", [where, p]);
                }
                // A local in the predicate is a value the loop computes, which no element binding reaches.
                where = Exprs.Replace(where ?? new Lit("true"), s => s.Kind == "local" ? new Unknown("loop-local", s.Path) : null);
                result.Add(new CountLoop(name, found.Loop.Span.End, of, where, via, viaHash));
            }
            finally
            {
                element = null;
            }
        }
        return result;
    }

    private static bool Writes(SyntaxNode n, string name)
    {
        var target = n switch
        {
            AssignmentExpressionSyntax a => a.Left,
            PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression) => p.Operand,
            PostfixUnaryExpressionSyntax q when q.IsKind(SyntaxKind.PostIncrementExpression) || q.IsKind(SyntaxKind.PostDecrementExpression) => q.Operand,
            ArgumentSyntax { RefKindKeyword.Text: "ref" or "out" } g => g.Expression,
            _ => null,
        };
        return target is IdentifierNameSyntax i && i.Identifier.Text == name;
    }

    private static bool Increments(ExpressionSyntax e) =>
        e.IsKind(SyntaxKind.PostIncrementExpression) || e.IsKind(SyntaxKind.PreIncrementExpression)
        || e is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Right: LiteralExpressionSyntax { Token.Text: "1" } };

    /// <summary>The loop whose body reaches <paramref name="step"/> through if/else nesting only, with the if conditions outermost first.</summary>
    private static (StatementSyntax Loop, StatementSyntax Statement, List<(ExpressionSyntax Cond, bool Positive)> Conds)? Enclosing(StatementSyntax step)
    {
        var conds = new List<(ExpressionSyntax, bool)>();
        SyntaxNode cur = step;
        while (cur.Parent is { } parent)
        {
            switch (parent)
            {
                case IfStatementSyntax i when cur == i.Statement:
                    conds.Add((i.Condition, true));
                    break;
                case ElseClauseSyntax { Parent: IfStatementSyntax i }:
                    conds.Add((i.Condition, false));
                    break;
                case IfStatementSyntax i when cur == i.Else:
                    break;
                case BlockSyntax b when !b.Statements.TakeWhile(s => s != cur).Any(s => s.DescendantNodesAndSelf().Any(n => n is ContinueStatementSyntax or BreakStatementSyntax
                    or ReturnStatementSyntax or GotoStatementSyntax or ThrowStatementSyntax or YieldStatementSyntax)):
                    break;
                case ForEachStatementSyntax f when cur == f.Statement:
                    conds.Reverse();
                    return (f, f.Statement, conds);
                case ForStatementSyntax f when cur == f.Statement:
                    conds.Reverse();
                    return (f, f.Statement, conds);
                default:
                    return null;
            }
            cur = parent;
        }
        return null;
    }

    /// <summary>The collection and index variable of <c>for (int i = 0; i &lt; C.Count; i++)</c> (or <c>C.Length</c>) whose body never writes i.</summary>
    private static (ExpressionSyntax Collection, string Index)? IndexedOver(ForStatementSyntax f)
    {
        if (f.Declaration is not { Variables.Count: 1 } d || d.Variables[0].Initializer?.Value is not LiteralExpressionSyntax { Token.Text: "0" }
            || f.Condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression, Left: IdentifierNameSyntax l, Right: MemberAccessExpressionSyntax r }
            || r.Name.Identifier.Text is not ("Count" or "Length") || f.Incrementors.Count != 1 || !Increments(f.Incrementors[0]))
        {
            return null;
        }
        var i = d.Variables[0].Identifier.Text;
        return l.Identifier.Text == i && Writes(f.Incrementors[0], i) && !f.Statement.DescendantNodes().Any(n => Writes(n, i)) ? (r.Expression, i) : null;
    }

    /// <summary>The collection a count loop iterates: a local's initializer when nothing else writes it, else the expression.</summary>
    private Expr Over(SyntaxNode member, ExpressionSyntax collection, int at)
    {
        if (collection is IdentifierNameSyntax id && Local(member, id.Identifier.Text, at) is { Kind: "local" } local)
        {
            return local.Init is { } init && !member.DescendantNodes().Any(n => Writes(n, id.Identifier.Text))
                ? Eval(member, init, local.Position, 0).E
                : new Unknown("local-collection", Squash(collection));
        }
        return Eval(member, collection, at, 0).E;
    }

    private Expr Predicate(SyntaxNode member, ExpressionSyntax cond, string? worker, ref string? via, ref string? hash)
    {
        switch (cond)
        {
            case ParenthesizedExpressionSyntax p:
                return Predicate(member, p.Expression, worker, ref via, ref hash);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return new Op("not", [Predicate(member, u.Operand, worker, ref via, ref hash)]);
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
                var left = Predicate(member, b.Left, worker, ref via, ref hash);
                var right = Predicate(member, b.Right, worker, ref via, ref hash);
                return new Op(b.IsKind(SyntaxKind.LogicalAndExpression) ? "and" : "or", [left, right]);
            case InvocationExpressionSyntax inv when Override(member, inv, worker) is { } o:
                if (o.Via is not null && via is not null && via != o.Via)
                {
                    return new Unknown("virtual-call", "second override " + o.Via);
                }
                (via, hash) = o.Via is null ? (via, hash) : (o.Via, o.Hash);
                return o.Expr;
            default:
                return Eval(member, cond, cond.SpanStart, 0).E;
        }
    }

    /// <summary>
    /// A virtual call on this whose one argument is the loop element, resolved through the def's worker class: the
    /// override's single returned expression, or an unknown virtual-call. Null when the call is not of that shape.
    /// </summary>
    private (Expr Expr, string? Via, string? Hash)? Override(SyntaxNode member, InvocationExpressionSyntax inv, string? worker)
    {
        var name = inv.Expression switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null || element is null || inv.ArgumentList.Arguments.Count != 1 || Squash(inv.ArgumentList.Arguments[0].Expression) != element.Text)
        {
            return null;
        }
        var owner = SourceIndex.OwnerOf(member);
        var declared = index.Find(owner, name, out _).Where(m => m.Node is MethodDeclarationSyntax md && Fits(md, 1)).ToList();
        if (!declared.Any(m => m.Virtual))
        {
            return null;
        }
        var unknown = new Unknown("virtual-call", declared[0].Owner + "." + name + "()");
        if (worker is null || !index.IsSubtype(worker, owner))
        {
            return (unknown, null, null);
        }
        var impl = index.Find(worker, name, out _).Where(m => m.Node is MethodDeclarationSyntax md && Fits(md, 1)).ToList();
        if (impl.Count != 1 || impl[0].Node is not MethodDeclarationSyntax body
            || (body.ExpressionBody?.Expression ?? (body.Body is { Statements: [ReturnStatementSyntax { Expression: { } r }] } ? r : null)) is not { } ret)
        {
            return (unknown, null, null);
        }
        return (Eval(body, ret, ret.SpanStart, 0).E, NameOf(body), Hash(body));
    }

    /// <summary>The element type of a collection whose declared type (a local's, field's or property's) is a one-argument generic or an array.</summary>
    private string? ElementType(SyntaxNode member, ExpressionSyntax collection, int at)
    {
        var t = DeclaredType(member, collection, at, 0);
        while (t is QualifiedNameSyntax q)
        {
            t = q.Right;
        }
        return t switch
        {
            GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } g => SourceIndex.TypeName(g.TypeArgumentList.Arguments[0]),
            ArrayTypeSyntax a => SourceIndex.TypeName(a.ElementType),
            NullableTypeSyntax { ElementType: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } g } => SourceIndex.TypeName(g.TypeArgumentList.Arguments[0]),
            _ => null,
        };
    }

    private TypeSyntax? DeclaredType(SyntaxNode member, ExpressionSyntax e, int at, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }
        if (e is IdentifierNameSyntax id && Local(member, id.Identifier.Text, at) is { } local)
        {
            return local.Type is { IsVar: false } t ? t : local.Init is { } init ? DeclaredType(member, init, local.Position, depth + 1) : null;
        }
        if (Eval(member, e, at, 0).E is not Sym { Kind: "field" or "prop" } s)
        {
            return null;
        }
        var dot = s.Decl.LastIndexOf('.');
        var found = index.Find(s.Decl[..dot], s.Decl[(dot + 1)..], out _);
        return (found.Count > 0 ? found[0].Node : null) switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } => vd.Type,
            PropertyDeclarationSyntax p => p.Type,
            _ => null,
        };
    }

    private static bool Exits(StatementSyntax s) => s switch
    {
        ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax or GotoStatementSyntax => true,
        BlockSyntax b => b.Statements.Count > 0 && Exits(b.Statements[^1]),
        IfStatementSyntax { Else: { } e } i => Exits(i.Statement) && Exits(e.Statement),
        _ => false,
    };

    private void Flatten(List<Guard> into, SyntaxNode member, ExpressionSyntax cond, bool positive, string origin)
    {
        var inner = cond;
        while (inner is ParenthesizedExpressionSyntax p)
        {
            inner = p.Expression;
        }
        switch (inner)
        {
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                Flatten(into, member, u.Operand, !positive, origin);
                break;
            case BinaryExpressionSyntax bin when (positive && bin.IsKind(SyntaxKind.LogicalAndExpression)) || (!positive && bin.IsKind(SyntaxKind.LogicalOrExpression)):
                Flatten(into, member, bin.Left, positive, origin);
                Flatten(into, member, bin.Right, positive, origin);
                break;
            default:
                var e = Eval(member, inner, inner.SpanStart, 0).E;
                into.Add(new Guard(positive ? e : new Op("not", [e]), origin));
                break;
        }
    }

    private Res Eval(SyntaxNode member, ExpressionSyntax e, int at, int depth)
    {
        if (depth > MaxDepth)
        {
            return Fail("depth-limit", e);
        }
        var d = depth + 1;
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                // Parentheses add no structure; decompiled nested conditionals carry many (#3324).
                return Eval(member, p.Expression, at, depth);
            case IdentifierNameSyntax or ElementAccessExpressionSyntax when element is { } el && Squash(e) == el.Text:
                return el.Res;
            case LiteralExpressionSyntax l:
                return new Res(new Lit(l.Token.Text), null);
            case DefaultExpressionSyntax:
                return new Res(new Lit("default"), null);
            case PrefixUnaryExpressionSyntax u:
                return new Res(new Op(u.IsKind(SyntaxKind.LogicalNotExpression) ? "not" : u.OperatorToken.Text, [Eval(member, u.Operand, at, d).E]), null);
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression) || b.IsKind(SyntaxKind.AsExpression):
                return new Res(new Op(b.OperatorToken.Text, [Eval(member, b.Left, at, d).E, b.Right is TypeSyntax rt ? TypeLit(rt) : new Unknown("pattern", Squash(b.Right))]), null);
            case BinaryExpressionSyntax b:
                var left = Eval(member, b.Left, at, d);
                var right = Eval(member, b.Right, at, d);
                var op = b.IsKind(SyntaxKind.LogicalAndExpression) ? "and" : b.IsKind(SyntaxKind.LogicalOrExpression) ? "or" : b.OperatorToken.Text;
                return new Res(new Op(op, [left.E, right.E]), op is "and" or "or" or "==" or "!=" or "<" or ">" or "<=" or ">=" ? new Ty("bool", false) : ArithmeticType(op, left.T, right.T));
            case IsPatternExpressionSyntax ip:
                return new Res(new Op("is", [Eval(member, ip.Expression, at, d).E, Pattern(ip.Pattern)]), null);
            case CastExpressionSyntax c:
                var inner = Eval(member, c.Expression, at, d);
                return new Res(new Op("cast", [TypeLit(c.Type), inner.E]), new Ty(SourceIndex.TypeName(c.Type) ?? "", false));
            case ConditionalExpressionSyntax t:
                return new Res(new Op("cond", [Eval(member, t.Condition, at, d).E, Eval(member, t.WhenTrue, at, d).E, Eval(member, t.WhenFalse, at, d).E]), null);
            case ThisExpressionSyntax:
                return new Res(new Sym("this", "param", SourceIndex.OwnerOf(member), SourceIndex.OwnerOf(member)), new Ty(SourceIndex.OwnerOf(member), false));
            case BaseExpressionSyntax:
                var baseOwner = SourceIndex.OwnerOf(member);
                return new Res(new Sym("base", "param", baseOwner, baseOwner), new Ty(baseOwner, false));
            case IdentifierNameSyntax id:
                return Identifier(member, id, at, d);
            case MemberAccessExpressionSyntax m:
                return MemberAccess(member, m, at, d);
            case InvocationExpressionSyntax inv:
                return Invocation(member, inv, at, d);
            case ElementAccessExpressionSyntax:
                return Fail("indexer", e);
            case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                return Fail("construction", e);
            case AnonymousFunctionExpressionSyntax:
                return Fail("lambda", e);
            default:
                return Fail("unsupported-syntax:" + e.Kind(), e);
        }
    }

    /// <summary>The result type of an arithmetic operator: the declared operator's return type on a declared left type, or the one primitive both sides share.</summary>
    private Ty? ArithmeticType(string op, Ty? left, Ty? right)
    {
        if (left is null)
        {
            return null;
        }
        if (index.HasType(left.Name))
        {
            var found = index.Find(left.Name, op, out _).Where(m => m.Kind == "operator").Select(m => m.Type).Distinct().ToList();
            return found.Count == 1 && found[0] is { } t ? new Ty(t, false) : null;
        }
        return right is not null && right.Name == left.Name && left.Name is "int" or "float" or "double" or "long" ? left : null;
    }

    private static Res Fail(string reason, SyntaxNode e) => new(new Unknown(reason, Squash(e)), null);

    private static string Squash(SyntaxNode e)
    {
        var text = string.Join(' ', e.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 120 ? text[..120] + "..." : text;
    }

    private Expr TypeLit(TypeSyntax t)
    {
        var name = SourceIndex.TypeName(t);
        return name is null ? new Unknown("unsupported-syntax:" + t.Kind(), Squash(t))
            : index.HasType(name) ? new Lit(name)
            : new Unknown("external-type", name);
    }

    private Expr Pattern(PatternSyntax p) => p switch
    {
        ConstantPatternSyntax { Expression: LiteralExpressionSyntax l } => new Lit(l.Token.Text),
        DeclarationPatternSyntax d => TypeLit(d.Type),
        TypePatternSyntax t => TypeLit(t.Type),
        UnaryPatternSyntax { Pattern: { } inner } => new Op("not", [Pattern(inner)]),
        _ => new Unknown("pattern", Squash(p)),
    };

    private Res Identifier(SyntaxNode member, IdentifierNameSyntax id, int at, int depth)
    {
        var name = id.Identifier.Text;
        if (Local(member, name, at) is { } local)
        {
            var type = local.Type is { IsVar: false } t ? SourceIndex.TypeName(t)
                : local.Init is { } init ? Eval(member, init, local.Position, depth).T?.Name : null;
            var ty = type is null ? null : new Ty(type, false);
            if (local.Kind == "local" && !IsWriteTarget(id) && Trace(member, name, local, at) is { } traced)
            {
                return new Res(traced.Mods.Aggregate(traced.Base, (acc, m) => new Op("+", [acc, new Op("cond", [m.When, m.Delta, new Lit("0")])])), ty);
            }
            return new Res(new Sym(name, local.Kind, "", type), ty);
        }
        var owner = SourceIndex.OwnerOf(member);
        var found = owner.Length > 0 ? index.Find(owner, name, out _) : [];
        if (found.Count > 0)
        {
            return Hit(name, found, new Ty(owner, false), id);
        }
        if (index.HasType(name) && !index.Ambiguous(name))
        {
            return new Res(new Sym(name, "type", name, name), new Ty(name, true));
        }
        return Fail(index.HasType(name) ? "ambiguous-type" : "unresolved-symbol", id);
    }

    private Res MemberAccess(SyntaxNode member, MemberAccessExpressionSyntax m, int at, int depth)
    {
        var recv = Eval(member, m.Expression, at, depth);
        var name = m.Name.Identifier.Text;
        if (recv.E is Unknown)
        {
            return new Res(new Unknown(((Unknown)recv.E).Reason, Squash(m)), null);
        }
        if (recv.T is null)
        {
            return Fail("unresolved-type", m);
        }
        if (!index.HasType(recv.T.Name))
        {
            return Fail("external-type", m) with { E = new Unknown("external-type", recv.T.Name + "." + name) };
        }
        if (index.Ambiguous(recv.T.Name))
        {
            return Fail("ambiguous-type", m);
        }
        var found = index.Find(recv.T.Name, name, out var external);
        if (found.Count == 0)
        {
            return Fail(external ? "external-type" : "unresolved-symbol", m) with
            {
                E = new Unknown(external ? "external-type" : "unresolved-symbol", recv.T.Name + "." + name),
            };
        }
        return Hit(Exprs.Render(recv.E) + "." + name, found, recv.T, m);
    }

    private Res Invocation(SyntaxNode member, InvocationExpressionSyntax inv, int at, int depth)
    {
        string name;
        Res recv;
        switch (inv.Expression)
        {
            case IdentifierNameSyntax id:
                name = id.Identifier.Text;
                var owner = SourceIndex.OwnerOf(member);
                recv = new Res(new Sym("this", "param", owner, owner), new Ty(owner, false));
                break;
            case MemberAccessExpressionSyntax m:
                name = m.Name.Identifier.Text;
                recv = Eval(member, m.Expression, at, depth);
                break;
            default:
                return Fail("unsupported-syntax:" + inv.Expression.Kind(), inv);
        }
        if (recv.E is Unknown u)
        {
            return new Res(new Unknown(u.Reason, Squash(inv)), null);
        }
        if (recv.T is null || !index.HasType(recv.T.Name))
        {
            return Fail(recv.T is null ? "unresolved-type" : "external-type", inv) with
            {
                E = new Unknown(recv.T is null ? "unresolved-type" : "external-type", recv.T is null ? Squash(inv) : recv.T.Name + "." + name),
            };
        }
        var arity = inv.ArgumentList.Arguments.Count;
        var found = index.Find(recv.T.Name, name, out _).Where(f => f.Node is MethodDeclarationSyntax md && Fits(md, arity)).ToList();
        if (found.Count == 0)
        {
            found = [.. index.FindExtension(recv.T.Name, name).Where(f => Fits((MethodDeclarationSyntax)f.Node, arity + 1))];
        }
        if (found.Count == 0)
        {
            return Fail("unresolved-symbol", inv) with { E = new Unknown("unresolved-symbol", recv.T.Name + "." + name + "()") };
        }
        if (found.Any(f => f.Virtual) && inv.Expression is not MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax })
        {
            return Fail("virtual-call", inv) with { E = new Unknown("virtual-call", found[0].Owner + "." + name + "()") };
        }
        // A stat read (#3324), x.GetStatValue(StatDefOf.S) with post-processing, is one symbol: kind stat, decl StatDefOf.S.
        if (name == "GetStatValue" && found[0].Owner == "StatExtension" && recv.T is { IsStatic: false } && inv.ArgumentList.Arguments is [var stat, ..]
            && Eval(member, stat.Expression, at, depth).E is Sym { Kind: "field", Decl: var statDecl } && statDecl.StartsWith("StatDefOf.", StringComparison.Ordinal)
            && !inv.ArgumentList.Arguments.Skip(1).Any(a => a.Expression is LiteralExpressionSyntax { Token.Text: "false" }))
        {
            return new Res(new Sym(Exprs.Render(recv.E) + ".GetStatValue(" + statDecl + ")", "stat", statDecl, "float"), new Ty("float", false));
        }
        var hit = Hit(Exprs.Render(recv.E) + "." + name, found, recv.T, inv);
        var args = inv.ArgumentList.Arguments.Select(a => Eval(member, a.Expression, at, depth).E);
        return new Res(new Op("call", [hit.E, .. args]), hit.T);
    }

    private static bool Fits(MethodDeclarationSyntax md, int arity)
    {
        var ps = md.ParameterList.Parameters;
        return ps.Count == arity || (ps.Count >= arity && ps.Skip(arity).All(p => p.Default is not null));
    }

    private static Res Hit(string path, IReadOnlyList<Member> found, Ty receiver, SyntaxNode at)
    {
        var first = found[0];
        if (first.Kind == "method" && at is not InvocationExpressionSyntax)
        {
            return Fail("method-group", at);
        }
        var types = found.Select(f => f.Type).Distinct().ToList();
        var type = types.Count == 1 ? types[0] : null;
        var isStatic = first.Kind == "type";
        var kind = first.Kind;
        return new Res(new Sym(path, kind, first.Owner + "." + first.Name, type), type is null ? null : new Ty(type, isStatic));
    }

    private sealed record LocalInfo(string Kind, TypeSyntax? Type, ExpressionSyntax? Init, int Position, SyntaxNode? Decl = null);

    private static LocalInfo? Local(SyntaxNode member, string name, int at)
    {
        LocalInfo? best = null;
        foreach (var node in member.DescendantNodesAndSelf())
        {
            LocalInfo? c = node switch
            {
                ParameterSyntax p when p.Identifier.Text == name => new LocalInfo("param", p.Type, null, p.SpanStart),
                VariableDeclaratorSyntax v when v.Identifier.Text == name && v.Parent is VariableDeclarationSyntax vd && vd.Parent is not FieldDeclarationSyntax
                    && Scoped(v, at) => new LocalInfo("local", vd.Type, v.Initializer?.Value, v.SpanStart, v),
                ForEachStatementSyntax f when f.Identifier.Text == name && f.Span.Contains(at) => new LocalInfo("local", f.Type, null, f.SpanStart),
                SingleVariableDesignationSyntax s when s.Identifier.Text == name && Scoped(s, at) => DesignationType(s),
                _ => null,
            };
            if (c is not null && c.Position <= at && (best is null || c.Position >= best.Position))
            {
                best = c;
            }
        }
        return best;
    }

    /// <summary>A traced local (#3324): its last straight-line value, plus each later if-guarded compound assignment's delta and condition.</summary>
    private sealed record Traced(Expr Base, IReadOnlyList<(Expr When, Expr Delta)> Mods);

    /// <summary>
    /// The value a declared local holds at <paramref name="at"/>, from syntax only (#3324): the initializer, replaced by
    /// each later plain assignment and shifted by each later <c>+=</c>, <c>-=</c>, <c>++</c> or <c>--</c>. A write
    /// counts only when it ends before <paramref name="at"/> in an earlier statement of a block enclosing
    /// <paramref name="at"/>, reached through blocks only (a plain assignment) or blocks and if/else (a compound
    /// assignment, guarded by those conditions). Null, so the local stays untraced, when it has no initializer, the
    /// member has a goto, the read is in a lambda or local function the declaration is outside, or any write is a ref/out
    /// argument, a tuple target, another compound operator, inside a loop or lambda the declaration is outside, or off those paths.
    /// </summary>
    private Traced? Trace(SyntaxNode member, string name, LocalInfo local, int at)
    {
        if (local.Decl is not VariableDeclaratorSyntax decl || decl.Initializer?.Value is not { } init
            || member.DescendantNodes().Any(n => n is GotoStatementSyntax or LabeledStatementSyntax))
        {
            return null;
        }
        var scope = decl.Ancestors().FirstOrDefault(a => a is BlockSyntax or SwitchSectionSyntax) ?? member;
        if (scope.DescendantNodes().Any(n => n is AssignmentExpressionSyntax { Left: TupleExpressionSyntax t }
            && t.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == name)))
        {
            return null;
        }
        var before = new List<SyntaxNode>();
        // A lambda reading a captured local runs later: the initializer (a factory call, say) was evaluated at capture time,
        // so substituting it would re-evaluate it in another context.
        if (member.FindToken(at).Parent?.Ancestors().TakeWhile(a => a != scope).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) == true)
        {
            return null;
        }
        foreach (var w in scope.DescendantNodes().Where(n => Writes(n, name)))
        {
            if (w is ArgumentSyntax || w.Ancestors().TakeWhile(a => a != scope).Any(a => a is ForStatementSyntax or CommonForEachStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                return null;
            }
            if (w.SpanStart > decl.SpanStart && w.Span.End <= at)
            {
                before.Add(w);
            }
        }
        var value = Eval(member, init, local.Position, 0).E;
        var mods = new List<(Expr When, Expr Delta)>();
        foreach (var w in before)
        {
            var lca = w.Ancestors().FirstOrDefault(a => a.Span.Contains(at));
            if (lca is not BlockSyntax || w.Parent is not ExpressionStatementSyntax)
            {
                return null;
            }
            var path = w.Ancestors().Skip(1).TakeWhile(a => a != lca).ToList();
            var guarded = path.Any(a => a is IfStatementSyntax or ElseClauseSyntax);
            if (path.Any(a => a is not (BlockSyntax or IfStatementSyntax or ElseClauseSyntax)))
            {
                return null;
            }
            if (w is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } plain && !guarded)
            {
                (value, mods) = (Eval(member, plain.Right, plain.Right.SpanStart, 0).E, []);
                continue;
            }
            Expr? delta = w switch
            {
                AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } a => Eval(member, a.Right, a.Right.SpanStart, 0).E,
                AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SubtractAssignmentExpression } a => Negate(Eval(member, a.Right, a.Right.SpanStart, 0).E),
                PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => new Lit(w.IsKind(SyntaxKind.PreIncrementExpression) || w.IsKind(SyntaxKind.PostIncrementExpression) ? "1" : "-1"),
                _ => null,
            };
            if (delta is null)
            {
                return null;
            }
            if (guarded)
            {
                mods.Add((GuardsAt(w, lca).Select(g => g.Cond).Aggregate((a, b) => new Op("and", [a, b])), delta));
            }
            else
            {
                value = new Op("+", [value, delta]);
            }
        }
        return new Traced(value, mods);
    }

    private static Expr Negate(Expr e) => e is Lit { Text: var t } && Number(t) is { } n ? new Lit(Format(-n)) : new Op("-", [e]);

    private static string Format(double n) => n.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A numeric literal's value, without its type suffix (<c>10f</c> is 10).</summary>
    private static double? Number(string text) =>
        double.TryParse(text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'L', 'l', 'u', 'U'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool IsWriteTarget(IdentifierNameSyntax id) => id.Parent switch
    {
        AssignmentExpressionSyntax a => a.Left == id,
        PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => true,
        ArgumentSyntax { RefKindKeyword.Text: "ref" or "out" } => true,
        _ => false,
    };

    /// <summary>
    /// The stage band of a site returning <c>ThoughtState.ActiveAtStage(local)</c> (#3324): the local's traced value must be
    /// a nested conditional of integer stages over <c>input &lt; c</c> comparisons of one input (in any of its strict
    /// forms), and every if-guarded modifier a numeric literal. Null for any other shape; it is never guessed.
    /// </summary>
    public StageBand? StageBandAt(SyntaxNode member, ExpressionSyntax leaf)
    {
        if (leaf is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ActiveAtStage" } } inv
            || inv.ArgumentList.Arguments is not [{ Expression: IdentifierNameSyntax id }]
            || Local(member, id.Identifier.Text, id.SpanStart) is not { Kind: "local" } local
            || Trace(member, id.Identifier.Text, local, id.SpanStart) is not { } traced)
        {
            return null;
        }
        var mods = new List<(Expr When, double Delta)>();
        foreach (var (when, delta) in traced.Mods)
        {
            if (delta is not Lit l || Number(l.Text) is not { } d)
            {
                return null;
            }
            mods.Add((when, d));
        }
        var cuts = new SortedSet<double>();
        Expr? input = null;
        if (!Cuts(traced.Base, cuts, ref input) || input is null)
        {
            return null;
        }
        var bounds = cuts.ToList();
        var stages = new List<int>();
        for (var j = 0; j <= bounds.Count; j++)
        {
            // On the interval [bounds[j-1], bounds[j]), input < c holds exactly for c >= bounds[j].
            var upper = j < bounds.Count ? bounds[j] : double.PositiveInfinity;
            if (StageOn(traced.Base, upper) is not { } s)
            {
                return null;
            }
            stages.Add(s);
        }
        var thresholds = new List<(double Below, int Stage)>();
        for (var j = 0; j < bounds.Count; j++)
        {
            if (stages[j] != stages[j + 1])
            {
                thresholds.Add((bounds[j], stages[j]));
            }
        }
        return new StageBand(input, thresholds, stages[^1], mods);
    }

    /// <summary>The comparison as (input, cut, input &lt; cut holds), for the strict forms <c>x &lt; c</c>, <c>x &gt;= c</c>, <c>c &gt; x</c>, <c>c &lt;= x</c> and their negations.</summary>
    private static (Expr Input, double Cut, bool Less)? Compare(Expr cond) => cond switch
    {
        Op { Name: "not", Args: [var inner] } => Compare(inner) is { } c ? (c.Input, c.Cut, !c.Less) : null,
        Op { Name: "<" or ">=", Args: [var x, Lit l] } when Number(l.Text) is { } n => (x, n, ((Op)cond).Name == "<"),
        Op { Name: ">" or "<=", Args: [Lit l, var x] } when Number(l.Text) is { } n => (x, n, ((Op)cond).Name == ">"),
        _ => null,
    };

    private static bool Cuts(Expr e, SortedSet<double> cuts, ref Expr? input)
    {
        switch (e)
        {
            case Lit l when int.TryParse(l.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _):
                return true;
            case Op { Name: "cond", Args: [var cond, var yes, var no] } when Compare(cond) is { } c:
                if (input is not null && Exprs.Render(input) != Exprs.Render(c.Input))
                {
                    return false;
                }
                input = c.Input;
                cuts.Add(c.Cut);
                return Cuts(yes, cuts, ref input) && Cuts(no, cuts, ref input);
            default:
                return false;
        }
    }

    /// <summary>The stage the conditional gives on the interval below <paramref name="upper"/> and at or above the cut before it.</summary>
    private static int? StageOn(Expr e, double upper) => e switch
    {
        Lit l => int.Parse(l.Text, System.Globalization.CultureInfo.InvariantCulture),
        Op { Args: [var cond, var yes, var no] } => Compare(cond)!.Value is var c && (c.Cut >= upper) == c.Less ? StageOn(yes, upper) : StageOn(no, upper),
        _ => null,
    };

    private static bool Scoped(SyntaxNode decl, int at)
    {
        var scope = decl.Ancestors().FirstOrDefault(a => a is BlockSyntax or BaseMethodDeclarationSyntax or AnonymousFunctionExpressionSyntax);
        return scope is null || scope.Span.Contains(at);
    }

    private static LocalInfo DesignationType(SingleVariableDesignationSyntax s) => s.Parent switch
    {
        DeclarationPatternSyntax dp => new LocalInfo("local", dp.Type, null, s.SpanStart),
        DeclarationExpressionSyntax de => new LocalInfo("local", de.Type, null, s.SpanStart),
        _ => new LocalInfo("local", null, null, s.SpanStart),
    };
}
