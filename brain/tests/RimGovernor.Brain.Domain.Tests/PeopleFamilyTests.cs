using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The people family (#3304): people/dialog replaced the AnswerDialog incident Concern. It is unmet while a choice
/// dialog is open, unknown without the census; its answer is one Method per window and option, claims its window
/// and is the Need's alone among the Need owners.
/// </summary>
public sealed class PeopleFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public void DialogIsUnmetWhileADialogIsOpenAndUnknownWithoutTheCensus()
    {
        Need Derive(Fact<bool> open) => Assert.Single(new PeopleFamily().Derive(new PeopleFacts { DialogOpen = open }));

        Assert.Equal(Satisfaction.Unknown, Derive(Unknown<bool>()).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Derive(Known(true)).Satisfaction);
        Assert.Equal(Satisfaction.Met, Derive(Known(false)).Satisfaction);
        Assert.Equal(PeopleFamily.Dialog, Derive(Known(true)).Id);
        Assert.Empty(new PeopleFamily().Propose([Derive(Known(true))], new PeopleFacts { DialogOpen = Known(true) }));
    }

    [Fact]
    public void TheAnswerMethodIsOnePerWindowAndOption()
    {
        var ok = new DialogOption { Index = 0, Label = "OK", Selectable = true, Resolves = true };
        var method = PeopleFamily.DialogMethod(7, ok);
        Assert.StartsWith("dialog-", method.Value, StringComparison.Ordinal);
        Assert.Equal("dialog-".Length + 32, method.Value.Length);
        Assert.Equal(method, PeopleFamily.DialogMethod(7, ok));
        Assert.NotEqual(method, PeopleFamily.DialogMethod(8, ok));
        Assert.NotEqual(method, PeopleFamily.DialogMethod(7, ok with { Index = 1 }));
    }

    [Fact]
    public void AnAnswerClaimsItsWindowAndOnlyTheDialogNeedAnswersAWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        BridgeFunctions.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-people-dialog-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);

        var answer = journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-a"), "", Answer(new DialogAnswer(7, 0, "OK")), ct);
        Assert.Equal(ClaimOutcome.Admitted, answer.Outcome);
        Assert.Equal(PeopleFamily.Dialog.FilingKey, journal.PlanMethod(answer.Owner.Methods[0].Plan, ct)!.Concern);
        // Another option of the same window waits on the window's claim; another window is admitted.
        var held = journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-b"), "", Answer(new DialogAnswer(7, 1, "Research screen")), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("dialog/7", held.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-c"), "", Answer(new DialogAnswer(8, 0, "OK")), ct).Outcome);

        // No other Need answers a window; a joiner letter stays its own Need's.
        Assert.ThrowsAny<Exception>(() => journal.CommitClaimedMethod(AdminFamily.Burial, World, new MethodId("window"), "", Answer(new DialogAnswer(9, 0, "OK")), ct));
        Assert.Equal(ClaimOutcome.Admitted,
            journal.CommitClaimedMethod(JoinersFamily.Quests, World, new MethodId("letter"), "", Answer(DialogAnswer.NewJoinerLetterAnswer(7, "Accept", "token")), ct).Outcome);
    }

    private static PlanSpec Answer(DialogAnswer answer)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new PlanRevision(1), [DomainAction.NewDialogAnswerAction(new ActionId(id.Value + "-0"), answer)]);
    }
}
