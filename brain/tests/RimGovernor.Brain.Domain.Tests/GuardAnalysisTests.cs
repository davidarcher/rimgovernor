using Microsoft.CodeAnalysis.CSharp.Syntax;
using RimGovernor.Tools.Commands.ThoughtAudit;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// The thoughtaudit -guards spike (#3218) is compiled in from tools/rg, as the launcher's NewColony is; the snippet below stands
// in for the decompiled game so the test needs no install.
public sealed class GuardAnalysisTests
{
    private const string Snippet = """
        namespace Game;

        public class Pawn { public bool IsColonist; public bool Awake() => true; public Faction faction; }
        public class Faction { public bool IsPlayer; }
        public class Building
        {
            public bool firstTime;
            public virtual bool Allowed(Pawn p) => true;
            public int Count() => 1;

            public void Grant(Pawn hauler)
            {
                if (!firstTime || !hauler.IsColonist)
                {
                    return;
                }
                if (hauler.faction.IsPlayer && Mystery.Check(hauler) && Allowed(hauler) && hauler.Awake() && Missing > 3)
                {
                    Gain();
                }
            }

            private void Gain() { }
        }
        """;

    private static (GuardAnalysis Analysis, InvocationExpressionSyntax Grant) Setup()
    {
        var index = SourceIndex.Build([("snippet.cs", Snippet)]);
        var grant = index.Trees[0].GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single(i => i.Expression.ToString() == "Gain");
        return (new GuardAnalysis(index), grant);
    }

    [Fact]
    public void ResolvedConditionsKeepTheirSymbolsAndEarlyExitsAreNegated()
    {
        var (analysis, grant) = Setup();

        var guards = analysis.GuardsAt(grant).ToDictionary(g => Exprs.Render(g.Cond));

        Assert.Equal("early-exit", guards["firstTime"].Origin);
        Assert.Equal("early-exit", guards["hauler.IsColonist"].Origin);
        Assert.Equal("if", guards["hauler.faction.IsPlayer"].Origin);
        Assert.True(Exprs.Resolved(guards["hauler.faction.IsPlayer"].Cond));
        var sym = Assert.Single(Exprs.Syms(guards["hauler.faction.IsPlayer"].Cond));
        Assert.Equal("Faction.IsPlayer", sym.Decl);
    }

    [Fact]
    public void UnresolvedSymbolsAndVirtualCallsAreUnknownWithAReasonNeverGuessed()
    {
        var (analysis, grant) = Setup();

        var unknown = analysis.GuardsAt(grant)
            .Where(g => !Exprs.Resolved(g.Cond))
            .SelectMany(g => Exprs.Unknowns(g.Cond))
            .Select(u => (u.Reason, u.Text))
            .Order()
            .ToList();

        Assert.Equal([("unresolved-symbol", "Missing"), ("unresolved-symbol", "Mystery.Check(hauler)")], unknown.Where(u => u.Reason != "virtual-call").ToList());
        Assert.Contains(unknown, u => u.Reason == "virtual-call" && u.Text == "Building.Allowed()");
    }

    // #3243: the sarcophagus shape. firstBody is written only by TryAccept; the site's caller (Haul.Deposit) either never
    // calls it (unreachable) or calls it first (reachable).
    private const string Casket = """
        namespace Game;

        public class ThoughtDef { }
        public static class ThoughtDefOf { public static ThoughtDef Know; }
        public class Pawn { public void Gain(ThoughtDef def) { } }
        public class Casket
        {
            private bool everNonEmpty;
            private bool firstBody;

            public bool TryAccept()
            {
                firstBody = !everNonEmpty;
                everNonEmpty = true;
                return true;
            }

            public void HauledTo(Pawn hauler)
            {
                if (!firstBody)
                {
                    return;
                }
                hauler.Gain(ThoughtDefOf.Know);
            }
        }
        public static class Haul
        {
            public static void Deposit(Casket casket, Pawn hauler)
            {
                DEPOSIT
                casket.HauledTo(hauler);
            }
        }
        """;

    private static Card CasketCard(string deposit)
    {
        var index = SourceIndex.Build([("casket.cs", Casket.Replace("DEPOSIT", deposit, StringComparison.Ordinal))]);
        var def = new ThoughtDef("Know", "", "", new Dictionary<string, System.Xml.Linq.XElement>());
        return Assert.Single(new GuardCards(index, ".", new HashSet<string> { "Know" }).Build([def], new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>()));
    }

    [Fact]
    public void AFieldWrittenOnlyOffTheCallerPathIsUnreachable()
    {
        var card = CasketCard("");

        Assert.Equal(GuardCards.Unreachable, card.Status);
        Assert.Contains(GuardCards.NoWriterOnCallerPath, card.Reasons);
        var guard = Assert.Single(Assert.Single(card.Sites).Guards);
        Assert.Equal("firstBody", Exprs.Render(guard.Cond));
        Assert.Contains("writers: Casket.TryAccept;", guard.Unreachable, StringComparison.Ordinal);
        Assert.Contains("callers checked: Casket.HauledTo, Haul.Deposit", guard.Unreachable, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerThatCallsTheWriterFirstKeepsTheGuardReachable()
    {
        var card = CasketCard("casket.TryAccept();");

        Assert.Equal("resolved", card.Status);
        Assert.Null(Assert.Single(Assert.Single(card.Sites).Guards).Unreachable);
    }

    [Fact]
    public void ASiteReachableThroughADelegateIsNeverClaimedUnreachable()
    {
        var card = CasketCard("System.Action<Pawn> later = casket.HauledTo;");

        Assert.Equal("resolved", card.Status);
    }

    // #3281: the apparel-thought shape, generic. A base worker counts the worn items its virtual predicate accepts; the
    // overrides resolve it or not. InlineWorker counts in an indexed for loop with the predicate written inline.
    private const string Counter = """
        namespace Game;

        public struct ThoughtState { public static ThoughtState Inactive; public static ThoughtState ActiveAtStage(int i) => default; }
        public class Item { public bool Tainted; }
        public class Tracker { public List<Item> Worn; }
        public class Pawn { public Tracker gear; }
        public abstract class Worker { protected abstract ThoughtState CurrentStateInternal(Pawn p); }
        public abstract class CountWorker : Worker
        {
            protected abstract bool Counts(Item item);

            protected override ThoughtState CurrentStateInternal(Pawn p)
            {
                int n = 0;
                foreach (var item in p.gear.Worn)
                {
                    if (Counts(item))
                    {
                        n++;
                    }
                }
                if (n == 0)
                {
                    return ThoughtState.Inactive;
                }
                return ThoughtState.ActiveAtStage(n - 1);
            }
        }
        public class TaintWorker : CountWorker { protected override bool Counts(Item item) => item.Tainted; }
        public class BlockWorker : CountWorker { protected override bool Counts(Item item) { var t = item.Tainted; return t; } }
        public class InlineWorker : Worker
        {
            protected override ThoughtState CurrentStateInternal(Pawn p)
            {
                var worn = p.gear.Worn;
                int n = 0;
                for (int i = 0; i < worn.Count; i++)
                {
                    if (worn[i].Tainted)
                    {
                        n++;
                    }
                }
                return n >= 2;
            }
        }
        """;

    private static Card CounterCard(string worker)
    {
        var index = SourceIndex.Build([("counter.cs", Counter)]);
        var def = new ThoughtDef("D", worker, "", new Dictionary<string, System.Xml.Linq.XElement>());
        return Assert.Single(new GuardCards(index, ".", new HashSet<string> { "D" }).Build([def], new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>()));
    }

    [Fact]
    public void ACountLoopBecomesACountGuardWithThePredicateResolvedThroughTheWorkerOverride()
    {
        var card = CounterCard("TaintWorker");

        Assert.Equal("resolved", card.Status);
        var guard = Assert.Single(Assert.Single(card.Sites).Guards);
        Assert.Equal("count", guard.Origin);
        Assert.Equal("!(n == 0)", Exprs.Render(guard.Cond));
        var count = Assert.IsType<CountSpec>(guard.Count);
        Assert.Equal("Tracker.Worn", Assert.IsType<Sym>(count.Over).Decl);
        Assert.Equal("Item.Tainted", Assert.IsType<Sym>(count.Where).Decl);
        Assert.Equal("(n - 1)", Exprs.Render(count.Stage));
        Assert.Equal("TaintWorker.Counts", count.Via);
        Assert.NotNull(count.ViaHash);
    }

    [Fact]
    public void AnIndexedLoopWithAnInlinePredicateBecomesACountGuard()
    {
        var card = CounterCard("InlineWorker");

        Assert.Equal("resolved", card.Status);
        var guard = Assert.Single(Assert.Single(card.Sites).Guards);
        Assert.Equal(("count", "(n >= 2)"), (guard.Origin, Exprs.Render(guard.Cond)));
        Assert.Equal("Tracker.Worn", Assert.IsType<Sym>(guard.Count!.Over).Decl);
        Assert.Equal(("Item.Tainted", "worn[i].Tainted"), (Assert.IsType<Sym>(guard.Count.Where).Decl, Exprs.Render(guard.Count.Where)));
        Assert.Equal("0", Exprs.Render(guard.Count.Stage));
        Assert.Null(guard.Count.Via);
    }

    [Theory]
    [InlineData("BlockWorker")]
    [InlineData("CountWorker")]
    public void APredicateNoWorkerOverrideResolvesIsAnUnknownCountGuard(string worker)
    {
        var card = CounterCard(worker);

        Assert.Equal("unknown", card.Status);
        var guard = Assert.Single(Assert.Single(card.Sites).Guards);
        var where = Assert.IsType<Unknown>(guard.Count!.Where);
        Assert.Equal(("virtual-call", "CountWorker.Counts()"), (where.Reason, where.Text));
        Assert.Contains("virtual-call", card.Reasons);
    }

    [Fact]
    public void TheSameSourceYieldsTheSameHash()
    {
        var (first, grantA) = Setup();
        var (second, grantB) = Setup();

        Assert.Equal(GuardAnalysis.Hash(GuardAnalysis.MemberOf(grantA)), GuardAnalysis.Hash(GuardAnalysis.MemberOf(grantB)));
        Assert.Equal(first.GuardsAt(grantA).Select(g => Exprs.Render(g.Cond)), second.GuardsAt(grantB).Select(g => Exprs.Render(g.Cond)));
    }
}
