using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The in-game goals panel's model, edits, queue and status hand-off (#3497).</summary>
public sealed class GoalsViewModelTests
{
    private static readonly RimGovernor.Brain.Store.World Home = new(new("colony"), new("load"), new(1));

    private static GoalsRecord Record(params GoalRecord[] goals)
    {
        var record = new GoalsRecord { World = Home };
        foreach (var goal in goals)
        {
            record = record.WithNewGoal(goal);
        }
        return record;
    }

    private static GoalStatus Status(GoalRecord goal, GoalState state, string reason = "", string owner = "") =>
        new(goal, Satisfaction.Unmet, state, reason, new NeedId(owner), Known(1.0));

    [Fact]
    public void EveryStateRendersWithItsReasonAndAGoalNotYetReviewedIsPending()
    {
        var record = Record(
            new GoalRecord { Kind = GoalKind.Colonists, Count = 3 },
            new GoalRecord { Kind = GoalKind.StockCount, Target = "Steel", Count = 100 },
            new GoalRecord { Kind = GoalKind.ResearchProject, Target = "Smithing" },
            new GoalRecord { Kind = GoalKind.Skill, Target = "p1", Qualifier = "Shooting", Count = 5 },
            new GoalRecord { Kind = GoalKind.Caravan });
        var g = record.Goals;
        var statuses = new[]
        {
            Status(g[0], GoalState.Met), Status(g[1], GoalState.Pursuing, owner: "economy/resource"),
            Status(g[2], GoalState.Blocked, "facts_unread"), Status(g[3], GoalState.NoMethod, "no_pursuit"),
        };

        var rows = GoalsViewModel.From(new GoalsSnapshot(record, statuses, [])).Groups.Single().Rows;

        Assert.Equal(["Met", "Pursuing", "Blocked", "No method", GoalsViewModel.Pending], rows.Select(r => r.State));
        Assert.Equal(["", "economy/resource", "facts_unread", "no_pursuit", ""], rows.Select(r => r.Detail));
        Assert.Equal("Stock count", rows[1].Kind);
        Assert.Equal("Steel x100", rows[1].Target);
        Assert.Equal("p1 Shooting x5", rows[3].Target);
    }

    [Fact]
    public void RowsSortByImportanceThenThemeThenIdAndThemesCarryTheirProgress()
    {
        var record = Record(
            new GoalRecord { Kind = GoalKind.Colonists, Count = 1 },
            new GoalRecord { Kind = GoalKind.Colonists, Count = 2, Importance = Importance.High },
            new GoalRecord { Kind = GoalKind.Colonists, Count = 3, Theme = 100, DeadlineOverride = 12 },
            new GoalRecord { Kind = GoalKind.Colonists, Count = 4, Theme = 100, Importance = Importance.High });
        record = record with
        {
            NextId = 101,
            Themes = [new ThemeRecord { Id = 100, Name = "Start", Members = [3, 4], DeadlineDay = 20 }],
        };

        var model = GoalsViewModel.From(new GoalsSnapshot(record, [], [new ThemeStatus(100, 1, 2)]));

        Assert.Equal([null, 100], model.Groups.Select(x => x.ThemeId));
        Assert.Equal([2, 1], model.Groups[0].Rows.Select(r => r.Id));
        Assert.Equal("1/2 met", model.Groups[1].Progress);
        Assert.Equal([4, 3], model.Groups[1].Rows.Select(r => r.Id));
        Assert.Equal(["day 20", "day 12"], model.Groups[1].Rows.Select(r => r.Deadline));
    }

    [Fact]
    public void AddEditAndRemoveProduceValidRecordsAndRemoveLeavesTheTheme()
    {
        var start = Record();
        var added = GoalEdits.Add(start, new GoalDraft { Kind = GoalKind.StockCount, Target = " Steel ", Count = 50, Importance = Importance.High, DeadlineDay = 9 });
        Assert.True(added.Ok);
        added.Record.Validate();
        var goal = Assert.Single(added.Record.Goals);
        Assert.Equal(("Steel", 1), (goal.Target, goal.Id));
        Assert.Equal(2, added.Record.NextId);

        var edited = GoalEdits.Edit(added.Record, 1, GoalDraft.Of(goal) with { Count = 75, DeadlineDay = null });
        Assert.True(edited.Ok);
        edited.Record.Validate();
        Assert.Equal((75, (int?)null), (edited.Record.Goals[0].Count, edited.Record.Goals[0].DeadlineDay));

        var themed = edited.Record.WithNewTheme(new ThemeRecord { Name = "T", Members = [1] });
        themed = themed with { Goals = [themed.Goals[0] with { Theme = themed.Themes[0].Id }] };
        var removed = GoalEdits.Remove(themed, 1);
        Assert.True(removed.Ok);
        removed.Record.Validate();
        Assert.Empty(removed.Record.Goals);
        Assert.Empty(removed.Record.Themes[0].Members);
    }

    [Fact]
    public void ARefusedEditKeepsThePreviousRecordWithTheReason()
    {
        var start = Record(new GoalRecord { Kind = GoalKind.Colonists, Count = 3 });

        var negative = GoalEdits.Add(start, new GoalDraft { Kind = GoalKind.Colonists, Count = -1 });
        var longText = GoalEdits.Edit(start, 1, new GoalDraft { Kind = GoalKind.StockCount, Target = new string('x', GoalRecord.MaxText + 1) });
        var missing = GoalEdits.Remove(start, 77);

        Assert.All(new[] { negative, longText, missing }, e =>
        {
            Assert.False(e.Ok);
            Assert.Same(start, e.Record);
        });
        Assert.Equal("negative goal count", negative.Error);
        Assert.Equal("goal text too long", longText.Error);
    }

    [Fact]
    public void StatusBoardServesItsColonyOnlyAndTheHostViewJoinsItWithTheRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new GoalRecord { Id = 1, Kind = GoalKind.Colonists, Count = 3 };
        var board = new GoalStatusBoard();
        using var store = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-goalsview-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var goals = new HostGoals(store, () => Home, new WakeSignal(), board);
        Assert.Empty(goals.Current().Statuses);

        board.Publish(Home, [Status(goal, GoalState.Met)], [new ThemeStatus(5, 1, 1)]);

        var snapshot = goals.Current();
        Assert.Equal(GoalState.Met, Assert.Single(snapshot.Statuses).State);
        Assert.Equal(5, Assert.Single(snapshot.Themes).Id);
        Assert.Empty(board.Read(Home with { Colony = new("other") }).Goals);
    }

    [Fact]
    public async Task EditsMadeDuringASaveAreQueuedInOrderAndNeverLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var editor = new GatedEditor();
        var panel = new GoalsPanel(() => new FixedView(new GoalsSnapshot(Record(), [], [])), () => editor);
        await panel.RefreshAsync(ct);

        panel.Submit(r => GoalEdits.Add(r, new GoalDraft { Kind = GoalKind.Colonists, Count = 1 }));
        await editor.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        panel.Submit(r => GoalEdits.Add(r, new GoalDraft { Kind = GoalKind.Colonists, Count = 2 }));
        panel.Submit(r => GoalEdits.Add(r, new GoalDraft { Kind = GoalKind.Colonists, Count = -5 }));
        editor.Gate.SetResult();
        await WaitFor(() => panel.Message == "negative goal count", ct);

        Assert.Equal([1, 2], editor.Saved.Select(r => r.Goals.Count));
        Assert.Equal("negative goal count", panel.Message);
        Assert.Equal([1, 2], panel.Model!.Record.Goals.Select(x => x.Count));
    }

    [Fact]
    public async Task AValidationFailureFromTheHostIsShownAndTheModelKeepsThePreviousRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var editor = new GatedEditor { Refuse = "no colony is observed" };
        editor.Gate.SetResult();
        var panel = new GoalsPanel(() => new FixedView(new GoalsSnapshot(Record(), [], [])), () => editor);
        await panel.RefreshAsync(ct);

        panel.Submit(r => GoalEdits.Add(r, new GoalDraft { Kind = GoalKind.Colonists, Count = 1 }));
        await WaitFor(() => panel.Message.Length > 0, ct);

        Assert.Equal("no colony is observed", panel.Message);
        Assert.Empty(panel.Model!.Record.Goals);
    }

    [Fact]
    public void WithoutABrainThePanelHasNoModel()
    {
        var panel = new GoalsPanel(() => null, () => null);
        Assert.False(panel.BrainStarted);
        Assert.Null(panel.Model);
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, ct);
        }
    }

    private sealed class FixedView(GoalsSnapshot snapshot) : IGoalsView
    {
        public GoalsSnapshot Current() => snapshot;
    }

    private sealed class GatedEditor : IGoalsEditor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<GoalsRecord> Saved { get; } = [];

        public string? Refuse { get; init; }

        public async Task SaveAsync(GoalsRecord record, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken);
            if (Refuse is not null)
            {
                throw new DomainException(Refuse);
            }
            lock (Saved)
            {
                Saved.Add(record);
            }
        }
    }
}
