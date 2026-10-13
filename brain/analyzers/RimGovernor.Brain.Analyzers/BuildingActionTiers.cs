using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>
/// Construction tier completeness (RGARCH008, Go archgate.BuildingActionTiers, #2525; contract
/// docs/developers/contracts/construction-tiers.md). Every call of DomainAction.NewBuildingAction states its tier as a
/// ConstructionTier ladder member or through a Policy mapping function (RoomTier, PlannerTier, AdoptedTier,
/// DefenseBuildTier). There is no baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BuildingActionTiersAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Descriptor = new(
        "RGARCH008",
        "Construction tier is stated",
        "arch tiers: {0} (see docs/developers/contracts/construction-tiers.md)",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly string[] Mappings = { "RoomTier", "PlannerTier", "AdoptedTier", "DefenseBuildTier" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Check, SyntaxKind.InvocationExpression);
    }

    private static void Check(SyntaxNodeAnalysisContext context)
    {
        var call = (InvocationExpressionSyntax)context.Node;
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => null,
        };
        if (name != "NewBuildingAction"
            || context.SemanticModel.GetSymbolInfo(call, context.CancellationToken).Symbol is not IMethodSymbol { ContainingAssembly.Name: "RimGovernor.Brain.Domain" })
        {
            return;
        }

        var args = call.ArgumentList.Arguments;
        if (args.Count != 3)
        {
            Report(context, call, "no tier argument");
        }
        else if (!StatesTier(context, args[2].Expression) && !PassThrough(context, call))
        {
            Report(context, call, "tier is not a ladder constant or policy mapping: " + args[2].Expression);
        }
    }

    /// <summary>
    /// The one place that rebuilds an action whose tier was already stated: the store codec reading a stored row (a row
    /// without a tier does not load). Go also listed NewPlan; DomainAction is built only by its factories here, so no plan
    /// re-canonicalizes actions.
    /// </summary>
    private static bool PassThrough(SyntaxNodeAnalysisContext context, SyntaxNode call) =>
        Arch.FileKey(call.SyntaxTree, context.Compilation.AssemblyName ?? "") == "Store/Codec/DomainActionConverter.cs"
        && Arch.MemberName(call) == "DomainActionConverter.ReadBuilding";

    private static bool StatesTier(SyntaxNodeAnalysisContext context, ExpressionSyntax arg)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(arg, context.CancellationToken).Symbol;
        switch (symbol)
        {
            case IFieldSymbol { ContainingType.Name: "ConstructionTier", HasConstantValue: true }:
                return true;
            case IMethodSymbol method:
                return method.ContainingAssembly.Name == "RimGovernor.Brain.Policy" && Mappings.Contains(method.Name);
            default:
                return false;
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string reason)
    {
        var file = System.IO.Path.GetFileName(node.SyntaxTree.FilePath);
        context.ReportDiagnostic(Diagnostic.Create(Descriptor, node.GetLocation(), file + "|" + Arch.MemberName(node) + "|" + reason));
    }
}
