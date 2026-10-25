using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>
/// Rule 9, there is no unknown (RGARCH009, epic #3753), in every brain project except Acceptance*. Counted per file and
/// token, key "file|token": Fact (a name bound to Domain's Fact&lt;T&gt;, one of its members other than ValueOrDefault, or
/// Fact.Known), Unknown (Fact.Unknown&lt;T&gt;()), ValueOrDefault, UnknownMember (an enum member named Unknown or
/// Unspecified), NullableValue (in Policy, Domain and Contracts: a property or positional parameter of a record or class
/// typed as a nullable value type) and Discard (a catch-all <c>_ =&gt;</c> arm, <c>default:</c> or <c>case _:</c> label of
/// a switch over an enum or an abstract or sealed record declared in the repo).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnknownsAnalyzer : GateAnalyzer
{
    private const string FactType = "RimGovernor.Brain.Domain.Fact`1";
    private const string FactStatics = "RimGovernor.Brain.Domain.Fact";

    /// <summary>The identifier texts that can bind to Fact or its members; other names are not bound at all.</summary>
    private static readonly HashSet<string> FactNames = new(StringComparer.Ordinal)
    {
        "Fact", "IsKnown", "TryGetValue", "Value", "ValueOrDefault", "Known", "Unknown",
    };

    private static readonly HashSet<string> RecordProjects = new(StringComparer.Ordinal) { "Policy", "Domain", "Contracts", "GameFacts", "GameOrders" };

    public UnknownsAnalyzer()
        : base(9, "There is no unknown", "unknown")
    {
    }

    /// <summary>The number of lines the baseline started with (#3756); it only shrinks.</summary>
    protected override int BaselineCap => 1381;

    protected override bool Counted => true;

    protected override bool Applies(string shortName) => !shortName.StartsWith("Acceptance", StringComparison.Ordinal);

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        var fact = context.Compilation.GetTypeByMetadataName(FactType);
        var statics = context.Compilation.GetTypeByMetadataName(FactStatics);
        if (fact is not null && statics is not null)
        {
            context.RegisterSyntaxNodeAction(c => FactUse(c, run, fact, statics), SyntaxKind.IdentifierName, SyntaxKind.GenericName);
        }

        context.RegisterSyntaxNodeAction(
            c =>
            {
                var member = (EnumMemberDeclarationSyntax)c.Node;
                if (member.Identifier.ValueText is "Unknown" or "Unspecified")
                {
                    Hit(run, member, "UnknownMember");
                }
            },
            SyntaxKind.EnumMemberDeclaration);

        if (RecordProjects.Contains(Arch.ShortName(run.Assembly)))
        {
            context.RegisterSyntaxNodeAction(c => NullableMember(c, run), SyntaxKind.PropertyDeclaration, SyntaxKind.Parameter);
        }

        context.RegisterSyntaxNodeAction(c => Discard(c, run), SyntaxKind.SwitchExpressionArm, SyntaxKind.DefaultSwitchLabel, SyntaxKind.CasePatternSwitchLabel);
    }

    private static void Hit(GateRun run, SyntaxNode node, string token)
    {
        var key = Arch.FileKey(node.SyntaxTree, run.Assembly);
        if (key is not null)
        {
            run.Count(key + "|" + token, node.GetLocation());
        }
    }

    private static void FactUse(SyntaxNodeAnalysisContext context, GateRun run, INamedTypeSymbol fact, INamedTypeSymbol statics)
    {
        var name = context.Node switch
        {
            IdentifierNameSyntax i => i.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => "",
        };
        if (!FactNames.Contains(name))
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        switch (symbol)
        {
            case INamedTypeSymbol type when SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, fact):
                Hit(run, context.Node, "Fact");
                break;
            case IMethodSymbol or IPropertySymbol when SymbolEqualityComparer.Default.Equals(symbol.ContainingType?.OriginalDefinition, fact):
                Hit(run, context.Node, symbol.Name == "ValueOrDefault" ? "ValueOrDefault" : "Fact");
                break;
            case IMethodSymbol method when SymbolEqualityComparer.Default.Equals(method.ContainingType, statics):
                Hit(run, context.Node, method.Name == "Unknown" ? "Unknown" : "Fact");
                break;
        }
    }

    private static void NullableMember(SyntaxNodeAnalysisContext context, GateRun run)
    {
        ITypeSymbol? type;
        switch (context.Node)
        {
            case PropertyDeclarationSyntax property when property.Parent is ClassDeclarationSyntax or RecordDeclarationSyntax:
                type = context.SemanticModel.GetDeclaredSymbol(property, context.CancellationToken)?.Type;
                break;
            case ParameterSyntax { Parent: ParameterListSyntax { Parent: ClassDeclarationSyntax or RecordDeclarationSyntax } } parameter:
                type = context.SemanticModel.GetDeclaredSymbol(parameter, context.CancellationToken)?.Type;
                break;
            default:
                return;
        }

        if (type is { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            Hit(run, context.Node, "NullableValue");
        }
    }

    private static void Discard(SyntaxNodeAnalysisContext context, GateRun run)
    {
        ExpressionSyntax? governing = context.Node switch
        {
            SwitchExpressionArmSyntax { Pattern: DiscardPatternSyntax, WhenClause: null, Parent: SwitchExpressionSyntax s } => s.GoverningExpression,
            DefaultSwitchLabelSyntax { Parent.Parent: SwitchStatementSyntax s } => s.Expression,
            CasePatternSwitchLabelSyntax { Pattern: DiscardPatternSyntax, WhenClause: null, Parent.Parent: SwitchStatementSyntax s } => s.Expression,
            _ => null,
        };
        if (governing is null)
        {
            return;
        }

        var type = context.SemanticModel.GetTypeInfo(governing, context.CancellationToken).Type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        if (type is not null && IsRepoUnion(type))
        {
            Hit(run, context.Node, "Discard");
        }
    }

    /// <summary>An enum, or an abstract or sealed record, declared in a RimGovernor assembly (brain/src).</summary>
    private static bool IsRepoUnion(ITypeSymbol type) =>
        type.ContainingAssembly?.Name.StartsWith("RimGovernor.", StringComparison.Ordinal) == true
        && (type.TypeKind == TypeKind.Enum || (type.IsRecord && (type.IsAbstract || type.IsSealed)));
}
