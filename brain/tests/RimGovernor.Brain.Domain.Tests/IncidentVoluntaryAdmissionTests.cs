using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreventativeMoodCastCannotUseIncidentToBypassSharedLaborButCombatRetainsAuthority(bool combat)
    {
        var ability = new Ability(new("pawn"), AbilitySource.PsycastSource(combat ? "Stun" : "WordOfJoy"),
            AbilityTarget.AbilityPawnTarget(new("target")));
        Verify(combat ? ActiveCombat : EnsureMood, DomainAction.NewAbilityAction(new("action"), ability),
            combat, "unsupported_forecast_mood_cast");
    }

    /// <summary>The economy/caravan Need (#3316) admits a trade phase as its Method, or refuses it with <paramref name="reason"/> and binds nothing.</summary>
    private static void VerifyCaravan(DomainAction action, bool allowed, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-caravan-admission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
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

    private static void Verify(ConcernId kind, DomainAction action, bool allowed, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-incident-voluntary-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var incident = journal.OpenIncident(new(kind, new("colony"), "observed_need", World, new(100), []), ct);
        var plan = new PlanSpec(new("plan"), new(1), [action]);
        if (allowed)
        {
            var committed = journal.CommitIncidentMethod(incident.Incident.Id, new("method"), "", plan, ct);
            Assert.Equal(plan.Id, Assert.Single(committed.Methods).Plan);
        }
        else
        {
            var error = Assert.Throws<NotAdmittedException>(() =>
                journal.CommitIncidentMethod(incident.Incident.Id, new("method"), "", plan, ct));
            Assert.Contains(reason, error.Message);
            Assert.Empty(journal.LoadIncident(incident.Incident.Id, ct).Methods);
        }
    }
}
