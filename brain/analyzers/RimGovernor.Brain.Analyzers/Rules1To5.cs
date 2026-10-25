using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>
/// Rule 1, layers (RGARCH001). Project references are the layer order; this gate keeps them honest. A project may
/// reference only projects at or below its own layer, plus no role-denied edge. Keys are "From->To" with short assembly
/// names. References are read from the compilation, so a transitive reference counts as well (it can only be a climb or
/// denied edge if some direct reference already is).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LayersAnalyzer : GateAnalyzer
{
    /// <summary>The rule 1 order, lowest first; projects on one line share a layer. Projects outside it are free.</summary>
    private static readonly string[][] Layers =
    {
        new[] { "Domain" },
        new[] { "GameFacts" },
        new[] { "GameOrders" },
        new[] { "Policy" },
        new[] { "GameAccess", "Bridge" },
        new[] { "Store" },
       new[] { "Executor" },
        new[] { "Runtime" },
        new[] { "PlayerApi" },
        new[] { "Host" },
    };

    /// <summary>
    /// Role denials on top of the order. Bridge -> policy is not denied, because the bridge returns Policy types by
    /// design; store is denied the two transport projects.
    /// </summary>
    private static readonly string[][] Denied =
    {
        new[] { "Store", "GameAccess" },
        new[] { "Store", "Bridge" },
        new[] { "Store", "HttpChannel" },
       new[] { "Executor", "Store" },
        new[] { "PlayerApi", "Runtime" },
    };

    public LayersAnalyzer()
        : base(1, "Layers", "layer climb or denied project reference")
    {
    }

    protected override bool Applies(string shortName) => true;

    protected override void Collect(CompilationAnalysisContext context, GateRun run)
    {
        var from = Arch.ShortName(run.Assembly);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Layers.Length; i++)
        {
            foreach (var name in Layers[i])
            {
                rank[name] = i;
            }
        }

        var fromRank = rank.TryGetValue(from, out var fr) ? fr : -1;
        foreach (var reference in context.Compilation.ReferencedAssemblyNames)
        {
            if (!reference.Name.StartsWith(Arch.AssemblyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var to = Arch.ShortName(reference.Name);
            var climbs = fromRank >= 0 && rank.TryGetValue(to, out var tr) && tr > fromRank;
            var denied = Denied.Any(d => d[0] == from && d[1] == to);
            if (climbs || denied)
            {
                run.Add(from + "->" + to, Location.None);
            }
        }
    }
}

/// <summary>
/// Rule 2, policy is pure (RGARCH002). Policy code uses no file, network, process, environment or clock API. Search budgets count iterations, never wall time. TimeSpan stays legal
/// (a duration is a value). Keys are "file|ban Type" and "file|import Namespace".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PolicyPurityAnalyzer : GateAnalyzer
{
    private static readonly string[] BannedNamespaces = { "System.IO", "System.Net" };

    private static readonly HashSet<string> BannedTypes = new(StringComparer.Ordinal)
    {
        "System.DateTime",
        "System.DateTimeOffset",
        "System.TimeProvider",
        "System.TimeZoneInfo",
        "System.Environment",
        "System.Diagnostics.Stopwatch",
        "System.Diagnostics.Process",
        "System.Threading.Timer",
        "System.Threading.PeriodicTimer",
    };

    /// <summary>
    /// In-memory System.IO types: reading an embedded resource and building text in memory touch no file or
    /// device. A file, directory or stream-over-a-file type stays banned, and naming one is what a caller would have to do.
    /// </summary>
    private static readonly HashSet<string> PureTypes = new(StringComparer.Ordinal)
    {
        "System.IO.Stream",
        "System.IO.MemoryStream",
        "System.IO.TextReader",
        "System.IO.TextWriter",
        "System.IO.StreamReader",
        "System.IO.StringReader",
        "System.IO.StringWriter",
        "System.IO.Path",
    };

    /// <summary>The worker count of a deterministic fan-out.</summary>
    private static readonly HashSet<string> PureMembers = new(StringComparer.Ordinal)
    {
        "System.Environment.ProcessorCount",
    };

    private static readonly HashSet<string> BannedMembers = new(StringComparer.Ordinal)
    {
        "System.Threading.Thread.Sleep",
        "System.Threading.Tasks.Task.Delay",
    };

    public PolicyPurityAnalyzer()
        : base(2, "Policy is pure", "policy uses a clock, file, network or process API")
    {
    }

    protected override bool Applies(string shortName) => shortName == "Policy";

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        context.RegisterSyntaxNodeAction(
            c => CheckName(c, run),
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);
        context.RegisterSyntaxNodeAction(c => CheckUsing(c, run), SyntaxKind.UsingDirective);
    }

    private static bool InBannedNamespace(string ns)
    {
        foreach (var banned in BannedNamespaces)
        {
            if (ns == banned || ns.StartsWith(banned + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckUsing(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var use = (UsingDirectiveSyntax)context.Node;
        if (use.Name is null || use.Alias is not null || use.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            return;
        }

        var key = Arch.FileKey(use.SyntaxTree, run.Assembly);
        var name = use.Name.ToString();
        if (key is not null && InBannedNamespace(name))
        {
            run.Add(key + "|import " + name, use.GetLocation());
        }
    }

    private static void CheckName(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var key = Arch.FileKey(context.Node.SyntaxTree, run.Assembly);
        if (key is null || context.Node.Parent is UsingDirectiveSyntax)
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is null)
        {
            return;
        }

        if (symbol is INamedTypeSymbol && Arch.IsQualifier(context.Node))
        {
            return;
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            return;
        }

        var typeName = Metadata(type);
        var memberName = symbol is INamedTypeSymbol ? null : typeName + "." + symbol.Name;
        if (memberName is not null && PureMembers.Contains(memberName))
        {
            return;
        }

        if (PureTypes.Contains(typeName))
        {
            return;
        }

        if (memberName is not null && BannedMembers.Contains(memberName))
        {
            run.Add(key + "|ban " + memberName, context.Node.GetLocation());
        }
        else if (type.ContainingNamespace is { } ns && InBannedNamespace(ns.ToDisplayString()) && type.DeclaredAccessibility == Accessibility.Public)
        {
            run.Add(key + "|ban " + typeName, context.Node.GetLocation());
        }
        else if (BannedTypes.Contains(typeName))
        {
            run.Add(key + "|ban " + typeName, context.Node.GetLocation());
        }
    }

    private static string Metadata(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;
}

/// <summary>
/// Rule 3, one owner per exported type (RGARCH003). A public type name declared in two or more of Domain, Policy,
/// Bridge and its contracts fails unless listed. These form a reference chain, so the higher
/// project sees every collision with the lower ones and reports it: key "Higher|Name".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OneOwnerAnalyzer : GateAnalyzer
{
    private static readonly string[] Owners = { "RimGovernor.Brain.Domain", "RimGovernor.Brain.Policy", "RimGovernor.Brain.GameAccess", "RimGovernor.Brain.Bridge" };

    public OneOwnerAnalyzer()
        : base(3, "One owner per exported type", "exported type declared by more than one of domain, policy and bridge")
    {
    }

    protected override bool Applies(string shortName) => Owners.Contains(Arch.AssemblyPrefix + shortName);

    protected override void Collect(CompilationAnalysisContext context, GateRun run)
    {
        var compilation = context.Compilation;
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && Owners.Contains(assembly.Name))
            {
                AddPublicTypes(assembly.GlobalNamespace, lower);
            }
        }

        var own = new Dictionary<string, Location>(StringComparer.Ordinal);
        CollectOwn(compilation.Assembly.GlobalNamespace, own);
        var me = Arch.ShortName(run.Assembly);
        foreach (var pair in own)
        {
            if (lower.Contains(pair.Key))
            {
                run.Add(me + "|" + pair.Key, pair.Value);
            }
        }
    }

    private static void AddPublicTypes(INamespaceSymbol ns, HashSet<string> into)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    AddPublicTypes(child, into);
                    break;
                case INamedTypeSymbol type when type.DeclaredAccessibility == Accessibility.Public:
                    into.Add(type.Name);
                    break;
            }
        }
    }

    private static void CollectOwn(INamespaceSymbol ns, Dictionary<string, Location> into)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    CollectOwn(child, into);
                    break;
                case INamedTypeSymbol type when type.DeclaredAccessibility == Accessibility.Public && !type.IsImplicitlyDeclared:
                    var where = type.Locations.FirstOrDefault(l => l.IsInSource);
                    if (where is not null && !into.ContainsKey(type.Name))
                    {
                        into.Add(type.Name, where);
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// Rule 4, no swallowed errors (RGARCH004). The form of dropping an
/// error is a catch block with no statement. Key "file|Type.Member".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwallowedErrorsAnalyzer : GateAnalyzer
{
    public SwallowedErrorsAnalyzer()
        : base(4, "No swallowed errors", "empty catch block")
    {
    }

    protected override bool Applies(string shortName) => true;

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        context.RegisterSyntaxNodeAction(
            c =>
            {
                var clause = (CatchClauseSyntax)c.Node;
                if (clause.Block.Statements.Count != 0)
                {
                    return;
                }

                var key = Arch.FileKey(clause.SyntaxTree, run.Assembly);
                if (key is not null)
                {
                    run.Add(key + "|" + Arch.MemberName(clause), clause.GetLocation());
                }
            },
            SyntaxKind.CatchClause);
    }
}

/// <summary>
/// Rule 5, numbers (RGARCH005), in Policy and Runtime. No literal 10000 compared with or clamping a
/// count/target-named operand (use a named constant or delete the cap), and no sentinel standing for unknown
/// (1e12, 1L&lt;&lt;40, double.MaxValue). Key "file|Type.Member|token".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NumbersAnalyzer : GateAnalyzer
{
    private static readonly Regex CountName = new("target|count|need|want|short|total|stock|have|qty|amount|demand", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private const double Cap = 10000;
    private const double Trillion = 1e12;
    private const double Shifted = 1099511627776d;

    public NumbersAnalyzer()
        : base(5, "Numbers", "capped count or unknown sentinel")
    {
    }

    protected override bool Applies(string shortName) => shortName == "Policy" || shortName == "Runtime";

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        context.RegisterSyntaxNodeAction(
            c => Sentinel(c, run),
            SyntaxKind.NumericLiteralExpression,
            SyntaxKind.LeftShiftExpression,
            SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(
            c => Compared(c, run),
            SyntaxKind.LessThanExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression);
        context.RegisterSyntaxNodeAction(c => Clamped(c, run), SyntaxKind.InvocationExpression);
    }

    private static void Add(GateRun run, SyntaxNode node, string token)
    {
        var key = Arch.FileKey(node.SyntaxTree, run.Assembly);
        if (key is not null)
        {
            run.Add(key + "|" + Arch.MemberName(node) + "|" + token, node.GetLocation());
        }
    }

    private static void Sentinel(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var node = context.Node;
        if (node is MemberAccessExpressionSyntax access)
        {
            if (access.Name.Identifier.ValueText == "MaxValue" && context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol is IFieldSymbol { ContainingType.SpecialType: SpecialType.System_Double })
            {
                Add(run, node, "double.MaxValue");
            }

            return;
        }

        var value = Arch.NumberOf(context.SemanticModel, node);
        if (value == Trillion)
        {
            Add(run, node, "sentinel 1e12");
        }
        else if (value == Shifted)
        {
            Add(run, node, "sentinel 1<<40");
        }
    }

    private static string Names(SyntaxNode node) =>
        string.Join(" ", node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText));

    private static bool IsCap(SemanticModel model, SyntaxNode node) => Arch.NumberOf(model, node) == Cap;

    private static void Compared(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var expr = (BinaryExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        if ((IsCap(model, expr.Left) && CountName.IsMatch(Names(expr.Right))) || (IsCap(model, expr.Right) && CountName.IsMatch(Names(expr.Left))))
        {
            Add(run, expr, "10000 compared");
        }
    }

    private static void Clamped(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var call = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(call, context.CancellationToken).Symbol is not IMethodSymbol method
            || (method.Name != "Min" && method.Name != "Max" && method.Name != "Clamp")
            || method.ContainingType.Name is not ("Math" or "MathF" or "Int32" or "Int64" or "Double" or "Single" or "Decimal"))
        {
            return;
        }

        var lit = false;
        var other = false;
        foreach (var arg in call.ArgumentList.Arguments)
        {
            if (IsCap(context.SemanticModel, arg.Expression))
            {
                lit = true;
            }
            else if (CountName.IsMatch(Names(arg.Expression)))
            {
                other = true;
            }
        }

        if (lit && other)
        {
            Add(run, call, "10000 clamp");
        }
    }
}
