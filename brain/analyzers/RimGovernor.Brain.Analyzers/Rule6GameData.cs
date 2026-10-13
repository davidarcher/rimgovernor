using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>
/// Rule 6, game data is read, not retyped (RGARCH006). No quoted thing or terrain def name (a static field of the
/// game's ThingDefOf or TerrainDefOf, listed in data/defof_names.txt) and no literal equal to a mirrored GameConstants
/// value (an integer of at least 1000 that is not a power of ten or two, data/game_constants.txt). Read the fact from
/// the catalog, or register a judgment table in Policy's DefTables (a declaration whose member or type DefTables() names
/// is an allowed owner, as is Domain/GameTime.cs). Acceptance projects are exempt (their cases drive the real game), as are the Remote* tool-host projects (no game code).
/// Key "file|Type.Member|\"DefName\"" or "file|Type.Member|number".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GameDataAnalyzer : GateAnalyzer
{
    private static readonly Lazy<ImmutableHashSet<string>> DefNames = new(() => Lines("defof_names.txt"));

    private static readonly Lazy<ImmutableHashSet<double>> Constants = new(() =>
        Lines("game_constants.txt")
            .Select(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN)
            .Where(x => !double.IsNaN(x))
            .ToImmutableHashSet());

    public GameDataAnalyzer()
        : base(6, "Game data is read, not retyped", "retyped game def name or constant")
    {
    }

    /// <summary>Rule 6 baselines every literal that predates it, which outnumbers any other rule's legacy.</summary>
    protected override int BaselineCap => 450;

    protected override bool Applies(string shortName) => !shortName.StartsWith("Acceptance", StringComparison.Ordinal) && !shortName.StartsWith("Remote", StringComparison.Ordinal);

    private static ImmutableHashSet<string> Lines(string resource)
    {
        using var stream = typeof(GameDataAnalyzer).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return ImmutableHashSet<string>.Empty;
        }

        using var reader = new System.IO.StreamReader(stream);
        var set = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length > 0)
            {
                set.Add(t);
            }
        }

        return set.ToImmutable();
    }

    /// <summary>
    /// A value specific enough that a literal equal to it is a retyped game constant rather than a coincidence: an
    /// integer of at least 1000 that is not a power of ten or of two. Smaller values and fractions match too many
    /// unrelated literals to ratchet.
    /// </summary>
    internal static bool DistinctiveConstant(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x != Math.Truncate(x) || x < 1000)
        {
            return false;
        }

        if (Math.Pow(10, Math.Round(Math.Log10(x))) == x)
        {
            return false;
        }

        return x != Math.Pow(2, Math.Round(Math.Log(x, 2)));
    }

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        var shortName = Arch.ShortName(run.Assembly);
        var owners = shortName == "Policy" ? TableOwners(context.Compilation) : ImmutableHashSet<string>.Empty;
        context.RegisterSyntaxNodeAction(c => Check(c, run, owners), SyntaxKind.StringLiteralExpression, SyntaxKind.NumericLiteralExpression);
    }

    /// <summary>
    /// The identifiers DefTables() mentions, lower-cased on the first letter. A literal inside a declaration (or a type) of
    /// one of those names is the registered table itself: the dangling-reference gate checks every name in it against the
    /// recorded catalog.
    /// </summary>
    private static ImmutableHashSet<string> TableOwners(Compilation compilation)
    {
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.FilePath.EndsWith("DefTables.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "DefTables"))
            {
                foreach (var id in method.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    names.Add(id.Identifier.ValueText);
                }
            }
        }

        return names.ToImmutable();
    }

    private static bool Owned(SyntaxNode node, ImmutableHashSet<string> owners)
    {
        if (owners.IsEmpty)
        {
            return false;
        }

        foreach (var a in node.Ancestors())
        {
            var name = a switch
            {
                FieldDeclarationSyntax f => f.Declaration.Variables[0].Identifier.ValueText,
                PropertyDeclarationSyntax p => p.Identifier.ValueText,
                MethodDeclarationSyntax m => m.Identifier.ValueText,
                BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
                _ => null,
            };
            if (name is not null && owners.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private static void Check(SyntaxNodeAnalysisContext context, GateRun run, ImmutableHashSet<string> owners)
    {
        var node = context.Node;
        var key = Arch.FileKey(node.SyntaxTree, run.Assembly);
        if (key is null || key == "Domain/GameTime.cs" || node.Parent is AttributeArgumentSyntax)
        {
            return;
        }

        string? token = null;
        if (node is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } str)
        {
            var text = str.Token.ValueText;
            if (DefNames.Value.Contains(text))
            {
                token = "\"" + text + "\"";
            }
        }
        else if (Arch.NumberOf(context.SemanticModel, node) is { } x && DistinctiveConstant(x) && Constants.Value.Contains(x))
        {
            token = Arch.Number(x);
        }

        if (token is not null && !Owned(node, owners))
        {
            run.Add(key + "|" + Arch.MemberName(node) + "|" + token, node.GetLocation());
        }
    }
}
