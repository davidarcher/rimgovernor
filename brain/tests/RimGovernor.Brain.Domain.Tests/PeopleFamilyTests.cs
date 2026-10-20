using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The people family (#3304): people/dialog replaced the AnswerDialog incident Concern. It is unmet while a choice
/// dialog is open, unknown without the census; its answer is one Method per window and option, claims its window
/// and is the Need's alone among the Need owners. people/mood/&lt;pawn&gt; replaced the per-pawn mood incident.
/// </summary>
public sealed class PeopleFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PeopleNeeds Dialog = new() { Dialog = true };

    [Fact]
    public void DialogIsUnmetWhileADialogIsOpenAndUnknownWithoutTheCensus()
    {
        Need Derive(Fact<bool> open) => Assert.Single(new PeopleFamily(Dialog).Derive(new PeopleFacts { DialogOpen = open }));

        Assert.Equal(Satisfaction.Unknown, Derive(Unknown<bool>()).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Derive(Known(true)).Satisfaction);
        Assert.Equal(Satisfaction.Met, Derive(Known(false)).Satisfaction);
        Assert.Equal(PeopleFamily.Dialog, Derive(Known(true)).Id);
        Assert.Empty(new PeopleFamily(Dialog).Propose([Derive(Known(true))], new PeopleFacts { DialogOpen = Known(true) }));
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
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-people-dialog-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);

        var answer = journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-a"), "", Answer(new DialogAnswer(7, 0, "OK")), ct);
        Assert.Equal(ClaimOutcome.Admitted, answer.Outcome);
        Assert.Equal(PeopleFamily.Dialog.FilingKey, journal.PlanMethod(answer.Owner.Methods[0].Plan, ct)!.Concern);
        // Another option of the same window waits on the window's claim; another window is admitted.
        var held = journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-b"), "", Answer(new DialogAnswer(7, 1, "Research screen")), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("dialog/7", held.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(PeopleFamily.Dialog, World, new MethodId("dialog-c"), "", Answer(new DialogAnswer(8, 0, "OK")), ct).Outcome);

        // No other Need answers a window; a joiner letter stays its own Need's.
        var stray = journal.CommitClaimedMethod(AdminFamily.Burial, World, new MethodId("window"), "", Answer(new DialogAnswer(9, 0, "OK")), ct);
        Assert.Equal(ClaimOutcome.Refused, stray.Outcome);
        Assert.Contains("dialog_answer_not_owned", stray.Refusal!.Message);
        Assert.Equal(ClaimOutcome.Admitted,
            journal.CommitClaimedMethod(JoinersFamily.Quests, World, new MethodId("letter"), "", Answer(DialogAnswer.NewJoinerLetterAnswer(7, "Accept", "token")), ct).Outcome);
    }

    /// <summary>people/mood replaced the per-pawn mood incident: one Need per tracked pawn, its predicate the review's mood state.</summary>
    [Fact]
    public void MoodIsOneNeedPerTrackedPawnAndAnUnreadReviewIsOneUnknownNeed()
    {
        var mood = new PeopleNeeds { Mood = true };
        var census = Assert.Single(new PeopleFamily(mood).Derive(new PeopleFacts()));
        Assert.Equal(PeopleFamily.MoodCensus, census.Id);
        Assert.Equal(Satisfaction.Unknown, census.Satisfaction);

        var states = EquatableArray.From<MoodState>([AtRisk("a"), AtRisk("b") with { Active = false, Pawn = AtRisk("b").Pawn with { Mood = Known(.9) } }, AtRisk("c") with { Missing = true }]);
        var needs = new PeopleFamily(mood).Derive(new PeopleFacts { Mood = Known(states) });
        Assert.Equal([PeopleFamily.Mood(new("a")), PeopleFamily.Mood(new("b")), PeopleFamily.Mood(new("c"))], needs.Select(n => n.Id));
        Assert.Equal([Satisfaction.Unmet, Satisfaction.Met, Satisfaction.Unknown], needs.Select(n => n.Satisfaction));
        Assert.All(needs, n => Assert.True(PeopleFamily.IsMood(n.Id)));
        Assert.False(PeopleFamily.IsMood(PeopleFamily.Dialog));
        // The dialog Need is its own toggle's.
        Assert.DoesNotContain(new PeopleFamily(Dialog).Derive(new PeopleFacts { DialogOpen = Known(false), Mood = Known(states) }), n => PeopleFamily.IsMood(n.Id));
    }

    /// <summary>A provisioned mood defers to the owner Need only while that Need is at work; otherwise it is held as an unsupported cast or left to need relief.</summary>
    [Fact]
    public void AMoodDefersToItsOwnerNeedOnlyWhileThatNeedIsAtWork()
    {
        var provisioned = AtRisk("a") with { Provision = [new MoodProvision(ShelterFamily.Sleeping, -10)] };
        var deferred = MoodRules.MoodNeedProposal(provisioned, [ShelterFamily.Sleeping]);
        Assert.Equal(MoodMethodReason.Provisioned, deferred.Reason);
        Assert.Equal(ShelterFamily.Sleeping, deferred.Provider);
        Assert.Equal(MoodMethodReason.NoCause, MoodRules.MoodNeedProposal(provisioned, [RoomsFamily.Comfort]).Reason);
        var hungry = provisioned with { Causes = [new MoodCause { Need = MoodNeed.Food, Level = Known(.1) }] };
        Assert.Equal(MoodMethodReason.Relief, MoodRules.MoodNeedProposal(hungry, []).Reason);
        Assert.Equal(MoodMethodReason.Provisioned, MoodRules.MoodNeedProposal(hungry, [ShelterFamily.Sleeping]).Reason);
    }

    /// <summary>
    /// One mood bar (#3265, decision 26): a pawn within 0.15 of its threshold is owed relief whatever its prior state,
    /// and a pawn above it is not, active before or not; an unread need keeps its earlier cause.
    /// </summary>
    [Fact]
    public void MoodReliefIsOneThresholdWithNoEntryOrRecoveryBand()
    {
        MoodPawn Pawn(double mood) => AtRisk("a").Pawn with { Mood = Known(.15 + mood), Target = Known(.15 + mood) };
        bool Review(double mood, bool prior) =>
            MoodRules.ReviewMood(Known<EquatableArray<MoodPawn>>([Pawn(mood)]),
                new MoodHistory { States = prior ? [AtRisk("a")] : [] }).States.Any(s => s.Active);

        Assert.True(Review(.12, prior: false));
        Assert.True(Review(.12, prior: true));
        Assert.False(Review(.17, prior: true));
        Assert.False(Review(.17, prior: false));
        // A need between the cause bar and the old retained bar is no cause, even for a pawn active before.
        var rested = Pawn(.12) with { Rest = Known(.4) };
        var tired = new MoodHistory { States = [AtRisk("a") with { Causes = [new MoodCause { Need = MoodNeed.Rest, Level = Known(.2) }] }] };
        Assert.Empty(Assert.Single(MoodRules.ReviewMood(Known<EquatableArray<MoodPawn>>([rested]), tired).States).Causes);
        var unread = Pawn(.12) with { Rest = Unknown<double>() };
        Assert.Equal(MoodNeed.Rest, Assert.Single(Assert.Single(MoodRules.ReviewMood(Known<EquatableArray<MoodPawn>>([unread]), tired).States).Causes).Need);
    }

    private static MoodState AtRisk(string pawn) => new()
    {
        Pawn = new MoodPawn
        {
            Id = new(pawn), Mood = Known(.2), Threshold = Known(.15), Target = Known(.2), Mental = Known(false), Dead = Known(false),
            Downed = Known(false), Drafted = Known(false), PlayerForced = Known(false),
        },
        Active = true,
    };

    private static PlanSpec Answer(DialogAnswer answer)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new PlanRevision(1), [DomainAction.NewDialogAnswerAction(new ActionId(id.Value + "-0"), answer)]);
    }
}
