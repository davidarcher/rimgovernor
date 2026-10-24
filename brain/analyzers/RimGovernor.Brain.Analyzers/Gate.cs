using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>Names and helpers every gate shares.</summary>
internal static class Arch
{
    internal const string AssemblyPrefix = "RimGovernor.Brain.";
    internal const string RulesDoc = "docs/developers/architecture/rules.md";
    internal const string BaselineDir = "brain/analyzers/baseline";

    /// <summary>
    /// Gate 7 is the world-state ban, which is not review item 7 of rules.md: its messages and baseline say so.
    /// </summary>
    internal static string Label(int rule) => rule == 7 ? "arch world-state ban" : "arch rule " + rule.ToString(CultureInfo.InvariantCulture);

    internal static string BaselineFile(int rule) => rule == 7 ? "worldstate.txt" : "rule" + rule.ToString(CultureInfo.InvariantCulture) + ".txt";

    /// <summary>The assembly name without the RimGovernor.Brain. prefix ("Policy", "Bridge.Contracts").</summary>
    internal static string ShortName(string? assembly)
    {
        var name = assembly ?? "";
        return name.StartsWith(AssemblyPrefix, StringComparison.Ordinal) ? name.Substring(AssemblyPrefix.Length) : name;
    }

    /// <summary>
    /// True for a project built empty because RimWorldDir has no game (Game.props then compiles only
    /// Properties/Unavailable.cs): it has no source to judge, so its baseline entries are neither checked nor reported stale.
    /// With the game, the file is one source among many.
    /// </summary>
    internal static bool IsUnavailable(Compilation compilation)
    {
        var sources = compilation.SyntaxTrees.Where(t => !t.FilePath.Replace('\\', '/').Contains("/obj/")).ToList();
        return sources.Count == 1 && sources[0].FilePath.Replace('\\', '/').EndsWith("/Properties/Unavailable.cs", StringComparison.Ordinal);
    }

    /// <summary>
    /// The baseline path of a tree: "Policy/Clock/Window.cs" (project short name, then the path inside the project), or
    /// null for generated code and files outside the project directory.
    /// </summary>
    internal static string? FileKey(SyntaxTree tree, string assembly)
    {
        var path = tree.FilePath.Replace('\\', '/');
        if (path.Contains("/obj/") || path.EndsWith(".g.cs", StringComparison.Ordinal) || path.EndsWith(".generated.cs", StringComparison.Ordinal))
        {
            return null;
        }

        var marker = "/" + assembly + "/";
        var at = path.LastIndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : ShortName(assembly) + "/" + path.Substring(at + marker.Length);
    }

    /// <summary>"Type.Member" of the declaration enclosing a node, "-" outside any member.</summary>
    internal static string MemberName(SyntaxNode node)
    {
        foreach (var a in node.AncestorsAndSelf())
        {
            switch (a)
            {
                case ConstructorDeclarationSyntax c:
                    return TypeOf(c) + "." + c.Identifier.ValueText;
                case DestructorDeclarationSyntax d:
                    return TypeOf(d) + ".~" + d.Identifier.ValueText;
                case MethodDeclarationSyntax m:
                    return TypeOf(m) + "." + m.Identifier.ValueText;
                case OperatorDeclarationSyntax o:
                    return TypeOf(o) + ".operator" + o.OperatorToken.ValueText;
                case ConversionOperatorDeclarationSyntax cv:
                    return TypeOf(cv) + ".operator " + cv.Type;
                case PropertyDeclarationSyntax p:
                    return TypeOf(p) + "." + p.Identifier.ValueText;
                case IndexerDeclarationSyntax ix:
                    return TypeOf(ix) + ".this[]";
                case EventDeclarationSyntax ev:
                    return TypeOf(ev) + "." + ev.Identifier.ValueText;
                case FieldDeclarationSyntax f:
                    return TypeOf(f) + "." + f.Declaration.Variables[0].Identifier.ValueText;
                case EventFieldDeclarationSyntax ef:
                    return TypeOf(ef) + "." + ef.Declaration.Variables[0].Identifier.ValueText;
                case BaseTypeDeclarationSyntax:
                    return "-";
            }
        }

        return "-";
    }

    /// <summary>True when the name only qualifies a member access: <c>GenTicks</c> in GenTicks.X, or in Verse.GenTicks.X.</summary>
    internal static bool IsQualifier(SyntaxNode node) => node.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Expression == node => true,
        MemberAccessExpressionSyntax { Parent: MemberAccessExpressionSyntax outer } inner when inner.Name == node && outer.Expression == inner => true,
        _ => false,
    };

    private static string TypeOf(SyntaxNode member) => member.Parent is BaseTypeDeclarationSyntax t ? t.Identifier.ValueText : "-";

    /// <summary>The numeric value of a constant expression, or null when it is not a number.</summary>
    internal static double? NumberOf(SemanticModel model, SyntaxNode node)
    {
        var value = model.GetConstantValue(node);
        if (!value.HasValue || value.Value is null)
        {
            return null;
        }

        switch (value.Value)
        {
            case char:
            case bool:
            case string:
                return null;
            case IConvertible c:
                try
                {
                    return c.ToDouble(CultureInfo.InvariantCulture);
                }
                catch (InvalidCastException)
                {
                    return null;
                }
        }

        return null;
    }

    internal static string Number(double x) =>
        x == Math.Truncate(x) && Math.Abs(x) < 1e15 ? ((long)x).ToString(CultureInfo.InvariantCulture) : x.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// One gate over one compilation: the violation keys found, compared with the rule's baseline when the compilation ends.
/// The baseline may only shrink: a violation missing from it fails, and so does an entry of this project that no longer
/// occurs. A counted gate's lines are "key|count" instead: a key without a line fails, a count above its line fails, and
/// a count below its line fails too, so the line is lowered to what the code holds.
/// </summary>
public sealed class GateRun
{
    private readonly ConcurrentDictionary<string, Location> _found = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    internal GateRun(int rule, string assembly, ImmutableHashSet<string> baseline, Func<string, string> owner, bool counted = false)
    {
        Rule = rule;
        Assembly = assembly;
        Baseline = baseline;
        Owner = owner;
        Counted = counted;
    }

    internal bool Counted { get; }

    internal int Rule { get; }

    internal string Assembly { get; }

    internal ImmutableHashSet<string> Baseline { get; }

    internal Func<string, string> Owner { get; }

    /// <summary>Records one violation; the earliest location in source order is kept for the report.</summary>
    internal void Add(string key, Location location) =>
        _found.AddOrUpdate(key, location, (_, old) => Earlier(old, location));

    private static Location Earlier(Location a, Location b)
    {
        if (!a.IsInSource)
        {
            return b;
        }

        if (!b.IsInSource)
        {
            return a;
        }

        var c = string.CompareOrdinal(a.SourceTree?.FilePath, b.SourceTree?.FilePath);
        return c != 0 ? (c < 0 ? a : b) : (a.SourceSpan.Start <= b.SourceSpan.Start ? a : b);
    }

    /// <summary>Records one hit of a counted gate; the key's count is the number of hits.</summary>
    internal void Count(string key, Location location)
    {
        _counts.AddOrUpdate(key, 1, (_, n) => n + 1);
        Add(key, location);
    }

    internal void Report(DiagnosticDescriptor descriptor, Action<Diagnostic> report, string what)
    {
        if (Counted)
        {
            ReportCounts(descriptor, report, what);
            return;
        }

        var mine = Arch.ShortName(Assembly);
        foreach (var pair in _found.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!Baseline.Contains(pair.Key))
            {
                report(Diagnostic.Create(descriptor, pair.Value, $"{Arch.Label(Rule)} violated: {what}: {pair.Key} (see {Arch.RulesDoc})"));
            }
        }

        foreach (var entry in Baseline.OrderBy(e => e, StringComparer.Ordinal))
        {
            if (Owner(entry) == mine && !_found.ContainsKey(entry))
            {
                report(Diagnostic.Create(descriptor, Location.None,
                    $"{Arch.Label(Rule)}: baseline entry no longer occurs, delete it from {Arch.BaselineDir}/{Arch.BaselineFile(Rule)}: {entry}"));
            }
        }
    }

    private void ReportCounts(DiagnosticDescriptor descriptor, Action<Diagnostic> report, string what)
    {
        var mine = Arch.ShortName(Assembly);
        var file = Arch.BaselineDir + "/" + Arch.BaselineFile(Rule);
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in Baseline.Where(e => Owner(e) == mine).OrderBy(e => e, StringComparer.Ordinal))
        {
            var cut = entry.LastIndexOf('|');
            if (cut <= 0 || !int.TryParse(entry.Substring(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
            {
                report(Diagnostic.Create(descriptor, Location.None, $"{Arch.Label(Rule)}: baseline line is not key|count, fix it in {file}: {entry}"));
                continue;
            }

            lines[entry.Substring(0, cut)] = count;
        }

        foreach (var pair in _counts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var where = _found[pair.Key];
            if (!lines.TryGetValue(pair.Key, out var allowed))
            {
                report(Diagnostic.Create(descriptor, where, $"{Arch.Label(Rule)} violated: {what}: {pair.Key}|{pair.Value} has no baseline line (see {Arch.RulesDoc})"));
            }
            else if (pair.Value > allowed)
            {
                report(Diagnostic.Create(descriptor, where, $"{Arch.Label(Rule)} violated: {what}: {pair.Key} rose from {allowed} to {pair.Value} (see {Arch.RulesDoc})"));
            }
            else if (pair.Value < allowed)
            {
                report(Diagnostic.Create(descriptor, where, $"{Arch.Label(Rule)}: {pair.Key} fell from {allowed} to {pair.Value}, lower the line in {file} to {pair.Key}|{pair.Value}"));
            }
        }

        foreach (var pair in lines.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!_counts.ContainsKey(pair.Key))
            {
                report(Diagnostic.Create(descriptor, Location.None, $"{Arch.Label(Rule)}: baseline entry no longer occurs, delete it from {file}: {pair.Key}|{pair.Value}"));
            }
        }
    }
}

/// <summary>
/// Base of the shrink-only gates. Subclasses say which projects they judge and what they look for; this class loads
/// brain/analyzers/baseline/rule&lt;n&gt;.txt (an AdditionalFile), skips projects built empty, and reports.
/// </summary>
public abstract class GateAnalyzer : DiagnosticAnalyzer
{
    private readonly DiagnosticDescriptor _descriptor;

    protected GateAnalyzer(int rule, string title, string what)
    {
        Rule = rule;
        What = what;
        _descriptor = new DiagnosticDescriptor(
            "RGARCH" + rule.ToString("000", CultureInfo.InvariantCulture),
            title,
            "{0}",
            "Architecture",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "See " + Arch.RulesDoc + ".");
    }

    protected int Rule { get; }

    protected string What { get; }

    /// <summary>The most entries a baseline may hold before the rule needs review.</summary>
    protected virtual int BaselineCap => 200;

    /// <summary>Whether baseline lines are "key|count" (see <see cref="GateRun"/>); hits are recorded with GateRun.Count.</summary>
    protected virtual bool Counted => false;

    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(_descriptor);

    /// <summary>Whether the project with this short name ("Policy") is judged by the rule.</summary>
    protected abstract bool Applies(string shortName);

    /// <summary>The project a baseline entry belongs to; the default reads the text before the first '/', '|' or "->".</summary>
    protected virtual string OwnerOf(string entry)
    {
        var cut = entry.Length;
        foreach (var marker in new[] { "->", "|", "/" })
        {
            var at = entry.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && at < cut)
            {
                cut = at;
            }
        }

        return entry.Substring(0, cut);
    }

    /// <summary>Registers the collection actions of one compilation.</summary>
    protected virtual void Register(CompilationStartAnalysisContext context, GateRun run)
    {
    }

    /// <summary>Collection that needs the whole compilation (runs when it ends, before the verdict).</summary>
    protected virtual void Collect(CompilationAnalysisContext context, GateRun run)
    {
    }

    public sealed override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var assembly = start.Compilation.AssemblyName ?? "";
            if (!assembly.StartsWith(Arch.AssemblyPrefix, StringComparison.Ordinal) || !Applies(Arch.ShortName(assembly)) || Arch.IsUnavailable(start.Compilation))
            {
                return;
            }

            var baseline = LoadBaseline(start.Options, Rule, start.CancellationToken);
            var run = new GateRun(Rule, assembly, baseline, OwnerOf, Counted);
            Register(start, run);
            start.RegisterCompilationEndAction(end =>
            {
                Collect(end, run);
                run.Report(_descriptor, end.ReportDiagnostic, What);
                if (Arch.ShortName(assembly) == "Domain" && baseline.Count > BaselineCap)
                {
                    end.ReportDiagnostic(Diagnostic.Create(_descriptor, Location.None,
                        $"{Arch.Label(Rule)}: baseline has {baseline.Count} entries (>{BaselineCap}), stop and review the rule"));
                }
            });
        });
    }

    internal static ImmutableHashSet<string> LoadBaseline(AnalyzerOptions options, int rule, CancellationToken token)
    {
        var file = Arch.BaselineFile(rule);
        var set = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var text in options.AdditionalFiles)
        {
            if (!string.Equals(System.IO.Path.GetFileName(text.Path), file, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = text.GetText(token);
            if (source is null)
            {
                continue;
            }

            foreach (var line in source.Lines)
            {
                var entry = line.ToString().Trim();
                if (entry.Length > 0 && entry[0] != '#')
                {
                    set.Add(entry);
                }
            }
        }

        return set.ToImmutable();
    }
}
