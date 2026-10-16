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
/// (#3243) when no writer on the site's direct caller path can make the guard hold; its text says what was checked.
/// </summary>
internal sealed record Guard(Expr Cond, string Origin, string? Unreachable = null);

/// <summary>Renders, and inspects, expression trees.</summary>
internal static class Exprs
{
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

    private const int MaxDepth = 8;

    /// <summary>The guards on the path from the node's member entry to the node, outermost first.</summary>
    public IReadOnlyList<Guard> GuardsAt(SyntaxNode node)
    {
        var member = MemberOf(node);
        var levels = new List<List<Guard>>();
        var cur = node;
        while (cur.Parent is { } parent && cur != member)
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
                return Eval(member, p.Expression, at, d);
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
            return new Res(new Sym(name, local.Kind, "", type), type is null ? null : new Ty(type, false));
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

    private sealed record LocalInfo(string Kind, TypeSyntax? Type, ExpressionSyntax? Init, int Position);

    private static LocalInfo? Local(SyntaxNode member, string name, int at)
    {
        LocalInfo? best = null;
        foreach (var node in member.DescendantNodesAndSelf())
        {
            LocalInfo? c = node switch
            {
                ParameterSyntax p when p.Identifier.Text == name => new LocalInfo("param", p.Type, null, p.SpanStart),
                VariableDeclaratorSyntax v when v.Identifier.Text == name && v.Parent is VariableDeclarationSyntax vd && vd.Parent is not FieldDeclarationSyntax
                    && Scoped(v, at) => new LocalInfo("local", vd.Type, v.Initializer?.Value, v.SpanStart),
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
