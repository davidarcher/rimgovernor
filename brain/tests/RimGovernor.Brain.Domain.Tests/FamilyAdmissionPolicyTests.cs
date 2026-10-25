using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FamilyAdmissionPolicyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static Journal Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-family-admission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        return journal;
    }

    private static (NeedId Need, DomainAction Action) Proposal(ActionKind kind) => kind switch
    {
        ActionKind.ProductionBill => (AdminFamily.WorkLedger, DomainAction.NewProductionBillAction(new("bill"),
            new("bench", "TestRecipe", BillMode.StockTarget, 1))),
        ActionKind.ZoneCreate => (FoodRules.FoodSupplyNeed, DomainAction.NewZoneCreateAction(new("field"),
            new ZoneCreate(ZoneKind.Growing, "TestPlant", [new Cell(1, 1)]))),
        ActionKind.Acquisition => (FoodRules.FoodSupplyNeed, DomainAction.NewAcquisitionAction(new("forage"),
            new("plant", "TestPlant", new(1, 1)))),
        ActionKind.WorkAssignment => (AdminFamily.WorkAssignments, DomainAction.NewWorkAssignmentAction(new("assignment"),
            WorkAssignment.NewAreaAssignment(new("pawn"), false, "Safe"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string? Conflict(NeedId need, PlanSpec plan, ActionKind kind) => kind switch
    {
        ActionKind.ProductionBill => FamilyAdmissionRules.BillConflict(need, plan, true),
        ActionKind.ZoneCreate => FamilyAdmissionRules.ZoneConflict(need, plan, true),
        ActionKind.Acquisition => FamilyAdmissionRules.AcquisitionConflict(need, plan, true),
        ActionKind.WorkAssignment => FamilyAdmissionRules.WorkConflict(need, plan, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [InlineData(ActionKind.ProductionBill, true)]
    [InlineData(ActionKind.ProductionBill, false)]
    [InlineData(ActionKind.ZoneCreate, true)]
    [InlineData(ActionKind.ZoneCreate, false)]
    [InlineData(ActionKind.Acquisition, true)]
    [InlineData(ActionKind.Acquisition, false)]
    [InlineData(ActionKind.WorkAssignment, true)]
    [InlineData(ActionKind.WorkAssignment, false)]
    public void ProposalPolicyAndTransactionalAdmissionAgreeOnFamilyPermissions(ActionKind kind, bool permitted)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open();
        var (allowed, action) = Proposal(kind);
        var need = permitted ? allowed : ShelterFamily.Sleeping;
        var plan = new PlanSpec(new("plan"), new(1), [action]);
        var conflict = Conflict(need, plan, kind);
        if (permitted)
        {
            Assert.Null(conflict);
            Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(need, World, new("method"), "", plan, ct).Outcome);
        }
        else
        {
            Assert.NotNull(conflict);
            Assert.Equal(new ConflictException(conflict).Message, Assert.Throws<ConflictException>(() =>
                journal.CommitClaimedMethod(need, World, new("method"), "", plan, ct)).Message);
            Assert.Empty(journal.LoadNeed(need, World, ct).Methods);
        }
    }

    [Fact]
    public void PolicyApprovalCannotBypassAReviewDisabledBeforeCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open();
        var (need, action) = Proposal(ActionKind.Acquisition);
        var plan = new PlanSpec(new("plan"), new(1), [action]);
        Assert.Null(FamilyAdmissionRules.AcquisitionConflict(need, plan, true));
        journal.Review(new() { Revision = journal.LoadReview(ct).Revision, Current = World, Tick = new(101),
            Enabled = false, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var refused = journal.CommitClaimedMethod(need, World, new("method"), "", plan, ct);
        Assert.Equal(ClaimOutcome.Refused, refused.Outcome);
        Assert.Equal("need " + need.Value + " has no current enabled review", refused.Refusal!.Reason);
        Assert.Empty(journal.LoadNeed(need, World, ct).Methods);
    }

    [Fact]
    public void IndependentlyPermittedProposalsStillCompeteForOneSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open();
        var (food, action) = Proposal(ActionKind.Acquisition);
        var first = new PlanSpec(new("first"), new(1), [action]);
        var second = new PlanSpec(new("second"), new(1), [DomainAction.NewAcquisitionAction(new("other-forage"), action.Acquisition!)]);
        Assert.Null(FamilyAdmissionRules.AcquisitionConflict(food, first, true));
        Assert.Null(FamilyAdmissionRules.AcquisitionConflict(EconomyFamily.Resource, second, true));
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(food, World, new("first"), "", first, ct).Outcome);
        var conflict = journal.CommitClaimedMethod(EconomyFamily.Resource, World, new("second"), "", second, ct);
        Assert.Equal(ClaimOutcome.Conflict, conflict.Outcome);
        Assert.Equal("plant", conflict.Conflict!.Claim);
        Assert.Empty(journal.LoadNeed(EconomyFamily.Resource, World, ct).Methods);
    }
}
