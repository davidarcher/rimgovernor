using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RimGovernor.Brain.Analyzers;

/// <summary>Live activity stage dependency fence. No baseline or per-file exceptions.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ActivityStagesAnalyzer : DiagnosticAnalyzer
{
    private const string Root = "RimGovernor.Brain.Policy.Methods";
    private static readonly DiagnosticDescriptor Descriptor = new("RGARCH010", "Activity stage ownership",
        "activity stage {0} cannot depend on {1}", "Architecture", DiagnosticSeverity.Error, true);
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(c =>
        {
            // Binding every identifier in every project dominated compile time; only fenced owners need it.
            if (!IsFenced(c.ContainingSymbol)) return;
            var symbol = c.SemanticModel.GetSymbolInfo(c.Node, c.CancellationToken).Symbol;
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            Check(c.ContainingSymbol, symbol, c.Node.GetLocation(), c.ReportDiagnostic);
        }, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
        // Target-typed new has no type-name syntax. Inspect its resolved constructor too.
        context.RegisterOperationAction(c =>
        {
            var creation = (IObjectCreationOperation)c.Operation;
            Check(c.ContainingSymbol, creation.Constructor, creation.Syntax.GetLocation(), c.ReportDiagnostic);
        }, OperationKind.ObjectCreation);
        context.RegisterSyntaxNodeAction(c =>
        {
            if ((IsLifecycle(c.ContainingSymbol) || (c.ContainingSymbol?.ContainingNamespace.ToDisplayString() ?? "").StartsWith(Root + ".", StringComparison.Ordinal))
                && c.SemanticModel.GetTypeInfo(c.Node, c.CancellationToken).Type?.Name == "ForecastActivityOutcomeComparison")
                c.ReportDiagnostic(Diagnostic.Create(Descriptor, c.Node.GetLocation(), "Method policy", "rewriting forecast selection"));
        }, SyntaxKind.WithExpression);
        context.RegisterSyntaxNodeAction(c =>
        {
            if (IsLifecycle(c.ContainingSymbol))
                c.ReportDiagnostic(Diagnostic.Create(Descriptor, c.Node.GetLocation(), "lifecycle", "arithmetic; put valuation in Evaluation"));
        }, SyntaxKind.AddExpression, SyntaxKind.SubtractExpression, SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideExpression, SyntaxKind.ModuloExpression);
        context.RegisterSymbolAction(c =>
        {
            var type = (INamedTypeSymbol)c.Symbol;
            if (type.ContainingNamespace.ToDisplayString().StartsWith(Root, StringComparison.Ordinal)
                && type.DeclaredAccessibility == Accessibility.Public)
                c.ReportDiagnostic(Diagnostic.Create(Descriptor, type.Locations[0], "internal implementation", "public stage type"));
        }, SymbolKind.NamedType);
    }

    private static void Check(ISymbol? owner, ISymbol? target, Location location, Action<Diagnostic> report)
    {
        if (!IsFenced(owner) || target is null) return;
        var source = owner?.ContainingNamespace?.ToDisplayString() ?? "";
        var lifecycle = IsLifecycle(owner);
        if (target is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            || target is IMethodSymbol { MethodKind: MethodKind.LocalFunction }) return;
        var stage = lifecycle ? "Lifecycle" : source.Substring(Root.Length + 1).Split('.')[0];
        var type = target as INamedTypeSymbol ?? target.ContainingType;
        if (type is null) return;
        var ns = type.ContainingNamespace.ToDisplayString();
        var allowed = true;
        if (ns.StartsWith(Root + ".", StringComparison.Ordinal))
        {
            var other = ns.Substring(Root.Length + 1).Split('.')[0];
            allowed = lifecycle || stage == other;
        }
        else if (ns.StartsWith("RimGovernor.Brain.Policy", StringComparison.Ordinal))
        {
            // Data contracts are permitted; calls into the unpartitioned policy implementation
            // are not. This also catches aliases, method groups and helper/extension calls.
            // These helpers only inspect exact ownership or invoke the shared comparator.
            var entry = lifecycle && target is IMethodSymbol
                && ((type.Name == "ActivityContinuationRules" && target.Name is "MatchesActivityOwner" or "ReviewActivityOwnership")
                    || (type.Name is "ForecastActivityOutcomesRules" or "ForecastCoupledSelection" && target.Name == "CompareActivityOutcomes"));
            allowed = entry || !(type.IsStatic && type.Name.EndsWith("Rules", StringComparison.Ordinal)) || target is INamedTypeSymbol;
            if (!entry && target is IMethodSymbol { MethodKind: not MethodKind.Constructor }) allowed = false;
            if (type.Name == "ForecastActivityOutcomeComparison" && target is IMethodSymbol) allowed = false;
        }
        else if (stage != "Binding" && ns == "RimGovernor.Brain.Domain"
            && (!lifecycle || target is IMethodSymbol)
            && type.Name is "PawnActivities" or "PawnActivityOrder" or "PawnActivityStep" or "DomainAction") allowed = false;
        else if (stage == "Evaluation"
            && (ns.StartsWith("RimGovernor.Brain.GameFacts", StringComparison.Ordinal)
                || ns.StartsWith("RimGovernor.Brain.GameOrders", StringComparison.Ordinal))) allowed = false;
        if (!allowed) report(Diagnostic.Create(Descriptor, location, stage, target.ToDisplayString()));
    }

    private static bool IsFenced(ISymbol? owner) => IsLifecycle(owner)
        || (owner?.ContainingNamespace?.ToDisplayString() ?? "").StartsWith(Root + ".", StringComparison.Ordinal);

    private static bool IsLifecycle(ISymbol? owner)
    {
        // Local functions/lambdas must not evade the entry-point fence.
        for (var symbol = owner; symbol is not null; symbol = symbol.ContainingSymbol)
            if (symbol is IMethodSymbol { Name: "StartCoupledActivity", ContainingType.Name: "ForecastCoupledSelection" }
                or IMethodSymbol { Name: "ContinueCoordinatedActivity", ContainingType.Name: "CoordinatedActivitiesRules" }
                && symbol.ContainingNamespace.ToDisplayString() == "RimGovernor.Brain.Policy") return true;
        return false;
    }
}
