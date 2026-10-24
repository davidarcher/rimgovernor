using RimGovernor.Brain.Policy;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>
/// Checks the hand-kept guard tables in Brain.Policy (<see cref="MechanicCardGuards"/>, #3223): every bound decl names a
/// decl the shipped cards use, every site -> action-kind row names a card site (a
/// <see cref="MechanicCardGuards.DisallowCounted"/> row a site whose guards are all counts), and,
/// against the decompiled sources, every such decl and site is declared in the game. Pure; a domain test runs it over the
/// embedded cards, and <c>thoughtaudit -guards</c> runs it with the source index after writing the shipped file.
/// </summary>
internal static class GuardTableCheck
{
    /// <summary>Throws when the tables disagree with the shipped file at <paramref name="shipped"/> or with the sources.</summary>
    public static void Require(string shipped, SourceIndex index)
    {
        using var stream = File.OpenRead(shipped);
        var problems = Problems(MechanicCards.Parse(stream), MechanicCardGuards.BoundDecls, MechanicCardGuards.SiteActions, index);
        if (problems.Count > 0)
        {
            throw new ToolException("thoughtaudit -guards: the guard tables in MechanicCardGuards.cs disagree with the cards: " + string.Join("; ", problems));
        }
    }

    /// <summary>The table rows naming a decl or site absent from the cards or, given an index, from the decompiled sources.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, MechanicCard> cards, IEnumerable<string> bindings, IEnumerable<CardSiteAction> sites, SourceIndex? index)
    {
        var problems = new List<string>();
        var cardSites = cards.Values.SelectMany(c => c.Sites).ToList();
        var decls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in cardSites)
        {
            Collect(decls, site.Guards);
            Collect(decls, site.Writers);
            if (site.Stage is { } band)
            {
                // A stage band's symbols (#3324) are checked against the sources even when no table row binds them.
                var bandDecls = new HashSet<string>(StringComparer.Ordinal);
                Collect(bandDecls, band.Input);
                foreach (var m in band.Modifiers)
                {
                    Collect(bandDecls, m.When);
                }
                foreach (var decl in bandDecls.Order(StringComparer.Ordinal))
                {
                    var dot = decl.LastIndexOf('.');
                    if (index is not null && (dot <= 0 || index.Find(decl[..dot], decl[(dot + 1)..], out _).Count == 0))
                    {
                        problems.Add($"stage symbol {decl} of site {site.Site} is not declared in the decompiled sources");
                    }
                }
                decls.UnionWith(bandDecls);
            }
        }
        foreach (var decl in bindings)
        {
            Check(decl, decls.Contains(decl), "binding");
        }
        foreach (var row in sites)
        {
            var site = cardSites.FirstOrDefault(s => s.Site == row.Site);
            Check(row.Site, site is not null, "site");
            // A prevention row falsifies a count guard by disallowing what it counts: every card site it names must count.
            if (row.Constraint == MechanicCardGuards.DisallowCounted
                && cardSites.Where(s => s.Site == row.Site).Any(s => s.Guards.Count == 0 || s.Guards.Any(g => g.Count is null)))
            {
                problems.Add($"site {row.Site} has a guard that is not a count, so {MechanicCardGuards.DisallowCounted} cannot falsify it");
            }
        }
        return problems;

        void Check(string decl, bool inCards, string what)
        {
            if (!inCards)
            {
                problems.Add($"{what} {decl} is not in the card file");
            }
            var dot = decl.LastIndexOf('.');
            if (index is not null && (dot <= 0 || index.Find(decl[..dot], decl[(dot + 1)..], out _).Count == 0))
            {
                problems.Add($"{what} {decl} is not declared in the decompiled sources");
            }
        }
    }

    private static void Collect(HashSet<string> decls, IEnumerable<MechanicCardGuard> guards)
    {
        foreach (var g in guards)
        {
            Collect(decls, g.Expr);
            if (g.Count is { } c)
            {
                Collect(decls, c.Over);
                Collect(decls, c.Where);
            }
        }
    }

    private static void Collect(HashSet<string> decls, MechanicCardExpr e)
    {
        if (e.Decl is { Length: > 0 } d)
        {
            decls.Add(d);
        }
        foreach (var a in e.Args ?? [])
        {
            Collect(decls, a);
        }
    }

    private static void Collect(HashSet<string> decls, IEnumerable<MechanicCardField> fields)
    {
        foreach (var f in fields)
        {
            decls.Add(f.Field);
            foreach (var w in f.Writers)
            {
                Collect(decls, w.Guards);
            }
            Collect(decls, f.Follows);
        }
    }
}
