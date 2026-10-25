using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalWorkTests
{
    [Fact]
    public void WorkCatalogViewsKeepTheirNativeRowsAndManualModeThroughReplay()
    {
        var cooking = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Cooking", Priority = 2, Disabled = false };
        var modded = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "ModdedWork", Priority = 4, Disabled = true };
        var observed = new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(false, [cooking, modded]);
        var row = new WorkPriority { Observed = cooking, Skill = "Cooking", Order = 10 };
        var view = new PawnWorkTable.Works(observed, [row]);
        Assert.Same(observed, view.Observed);
        Assert.Same(cooking, row.Observed);
        Assert.False(view.Manual);
        Assert.Equal((WorkType.Cooking, 2, false), (row.Work, row.Priority, row.Disabled));

        var replay = Assert.IsType<RimGovernor.Brain.GameFacts.Observations.PawnWork.Works>(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.PawnWork>(OrdersJson.Serialize<RimGovernor.Brain.GameFacts.Observations.PawnWork>(observed)));
        Assert.Equal(observed, replay);
        Assert.Equal(modded, replay.Rows[1]);
        var replayRow = new WorkPriority { Observed = replay.Rows[0], Skill = row.Skill, Order = row.Order };
        Assert.Equal(row, replayRow);
        Assert.False(new PawnWorkTable.Works(replay, [replayRow]).Manual);
    }

    [Fact]
    public void JobViewKeepsForcedQueueAndTargetEvidenceThroughNativeReplay()
    {
        var observed = new RimGovernor.Brain.GameFacts.Observations.JobEvidence
        {
            DefName = "DoBill", WorkTypeDefName = "Cooking", PlayerForced = true, QueuedJobs = 3,
            Preemptible = false,
            TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase(new RimGovernor.Brain.GameFacts.Common.Ref { Id = "bench" }, new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new Cell(2, 4))) },
        };
        var worker = PawnFixtures.Worker("cook") with { Observed = PawnFixtures.Row("cook") with { Job = observed } };
        Assert.Same(observed, worker.Job.Observed);
        Assert.Equal(WorkType.Cooking, worker.Job.Work);
        Assert.Equal(new JobTarget.Spawned("bench", new Cell(2, 4)), worker.Job.Target);
        var replay = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.JobEvidence>(OrdersJson.Serialize(observed));
        Assert.Equal(observed, replay);
        Assert.Equal(worker.Job, new PawnJob { Observed = replay });
    }
}