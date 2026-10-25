using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalWorkTests
{
    [Fact]
    public void WorkCatalogViewsKeepTheirNativeRowsAndManualModeThroughReplay()
    {
        var cooking = new O.WorkSetting { DefName = "Cooking", Priority = 2, Disabled = false };
        var modded = new O.WorkSetting { DefName = "ModdedWork", Priority = 4, Disabled = true };
        var observed = new O.PawnWork.Works(false, [cooking, modded]);
        var row = new WorkPriority { Observed = cooking, Skill = "Cooking", Order = 10 };
        var view = new PawnWorkTable.Works(observed, [row]);
        Assert.Same(observed, view.Observed);
        Assert.Same(cooking, row.Observed);
        Assert.False(view.Manual);
        Assert.Equal((WorkType.Cooking, 2, false), (row.Work, row.Priority, row.Disabled));

        var replay = Assert.IsType<O.PawnWork.Works>(WireJson.Deserialize<O.PawnWork>(WireJson.Serialize<O.PawnWork>(observed)));
        Assert.Equal(observed, replay);
        Assert.Equal(modded, replay.Rows[1]);
        var replayRow = new WorkPriority { Observed = replay.Rows[0], Skill = row.Skill, Order = row.Order };
        Assert.Equal(row, replayRow);
        Assert.False(new PawnWorkTable.Works(replay, [replayRow]).Manual);
    }

    [Fact]
    public void JobViewKeepsForcedQueueAndTargetEvidenceThroughNativeReplay()
    {
        var observed = new O.JobEvidence
        {
            DefName = "DoBill", WorkTypeDefName = "Cooking", PlayerForced = true, QueuedJobs = 3,
            Preemptible = false,
            TargetA = new() { Target = new O.TargetRefTarget.EntityCase(new C.Ref { Id = "bench" }, new O.EntityPlace.OnMap(0, new Cell(2, 4))) },
        };
        var worker = PawnFixtures.Worker("cook") with { Observed = PawnFixtures.Row("cook") with { Job = observed } };
        Assert.Same(observed, worker.Job.Observed);
        Assert.Equal(WorkType.Cooking, worker.Job.Work);
        Assert.Equal(new JobTarget.Spawned("bench", new Cell(2, 4)), worker.Job.Target);
        var replay = WireJson.Deserialize<O.JobEvidence>(WireJson.Serialize(observed));
        Assert.Equal(observed, replay);
        Assert.Equal(worker.Job, new PawnJob { Observed = replay });
    }
}