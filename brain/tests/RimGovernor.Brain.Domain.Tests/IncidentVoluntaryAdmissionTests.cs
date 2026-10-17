using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class IncidentVoluntaryAdmissionTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Theory]
    [InlineData(TradeParticipantKind.Map, false)]
    [InlineData(TradeParticipantKind.Orbital, false)]
    [InlineData(TradeParticipantKind.Settlement, true)]
    public void TradeOpenDistinguishesPawnLaborFromExistingCaravanSession(TradeParticipantKind kind, bool allowed)
    {
        var trade = Trade.NewTradeOpen("trader", new("pawn"), false)
            .WithParticipant(new(kind, "trader", kind == TradeParticipantKind.Settlement ? "caravan" : ""));
        VerifyCaravan(DomainAction.NewTradeAction(new("action"), trade), allowed, "unsupported_shared_trade_labor");
    }

    [Theory]
    [InlineData(TradeOperationKind.SetLines)]
    [InlineData(TradeOperationKind.Accept)]
    [InlineData(TradeOperationKind.End)]
    public void ExistingTradeSessionOperationsRemainAdmissible(TradeOperationKind kind)
    {
        var trade = kind switch
        {
            TradeOperationKind.SetLines => Trade.NewTradeSetLines("trader", new("pawn"), [new("line", 1)], false),
            TradeOperationKind.Accept => Trade.NewTradeAccept("trader", new("pawn"), "signature", false, false),
            TradeOperationKind.End => Trade.NewTradeEnd("trader", new("pawn"), TradeEndKind.Cancel, false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        VerifyCaravan(DomainAction.NewTradeAction(new("action"), trade), true, "");
    }

    /// <summary>A pawn's people/mood Need (#3304) casts nothing: a preventative cast would bypass the shared labor forecast.</summary>
    [Fact]
    public void PreventativeMoodCastCannotUseTheMoodNeedToBypassSharedLabor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-mood-voluntary-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var mood = PeopleFamily.Mood(new("pawn"));
        var ability = new Ability(new("pawn"), AbilitySource.PsycastSource("WordOfJoy"), AbilityTarget.AbilityPawnTarget(new("target")));
        var plan = new PlanSpec(new("plan"), new(1), [DomainAction.NewAbilityAction(new("action"), ability)]);
        var error = Assert.Throws<NotAdmittedException>(() => journal.CommitClaimedMethod(mood, World, new("mood-cast"), "", plan, ct));
        Assert.Contains("unsupported_forecast_mood_cast", error.Message);
        Assert.Empty(journal.LoadNeed(mood, World, ct).Methods);
    }

    /// <summary>A combat cast keeps its authority as a <c>military/combat</c> Method (#3311).</summary>
    [Fact]
    public void CombatCastRetainsAuthorityUnderTheCombatNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-combat-voluntary-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var ability = new Ability(new("pawn"), AbilitySource.PsycastSource("Stun"), AbilityTarget.AbilityPawnTarget(new("target")));
        var plan = new PlanSpec(new("plan"), new(1), [DomainAction.NewAbilityAction(new("action"), ability)]);
        var committed = journal.CommitCombatMethod(World, new("permit-cast"), plan, ct);
        Assert.Equal(plan.Id, Assert.Single(committed.Methods).Plan);
    }

    /// <summary>The economy/caravan Need (#3316) admits a trade phase as its Method, or refuses it with <paramref name="reason"/> and binds nothing.</summary>
    private static void VerifyCaravan(DomainAction action, bool allowed, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-caravan-admission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var plan = new PlanSpec(new("plan"), new(1), [action]);
        if (allowed)
        {
            var committed = journal.CommitClaimedMethod(EconomyFamily.Caravan, World, new("method"), "", plan, ct);
            Assert.Equal(ClaimOutcome.Admitted, committed.Outcome);
            Assert.Equal(plan.Id, Assert.Single(committed.Owner.Methods).Plan);
            // The same MethodId while its plan is open continues; nothing is bound twice.
            Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(EconomyFamily.Caravan, World, new("method"), "", new PlanSpec(new("plan-2"), new(1), [action]), ct).Outcome);
        }
        else
        {
            var error = Assert.Throws<NotAdmittedException>(() =>
                journal.CommitClaimedMethod(EconomyFamily.Caravan, World, new("method"), "", plan, ct));
            Assert.Contains(reason, error.Message);
            Assert.Empty(journal.LoadNeed(EconomyFamily.Caravan, World, ct).Methods);
        }
    }
}
