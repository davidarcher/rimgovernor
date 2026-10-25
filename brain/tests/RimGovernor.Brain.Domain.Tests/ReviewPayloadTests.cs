using System.Text;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ReviewPayloadTests
{
    [Fact]
    public void FullReviewLargerThanOneMiBRetainsItsCompleteAdmissionEvidenceAfterReopen()
    {
        // #4015: a native seven-colonist review is 1,500,351 bytes. Complete native
        // observations and catalog costs must survive; their JSON size is not a plan count.
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-review-payload-" + Guid.NewGuid().ToString("N") + ".db");
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        var facts = WorkResourceTestFacts.Create("pawn");
        var buildings = EquatableArray.From(Enumerable.Range(0, 8000).Select(i => new WorkBuildingCost("building-" + i, "stone",
            [new(new("stone"), 20), new(new("steel"), 10)]) { Size = new(2, 3) }));
        facts = facts with { WorkAdmission = facts.WorkAdmission with { Buildings = buildings } };
        string recorded;
        using (var journal = Journal.Open(path, ct))
        {
            var result = journal.Review(new ReviewRequest { Current = world, Tick = new(100), Enabled = true,
                Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
            recorded = StoreJson.Serialize(result.Review);
            Assert.True(Encoding.UTF8.GetByteCount(recorded) > 1024 * 1024);
            Assert.Equal(buildings, result.Review.WorkEvidence!.Buildings);
        }
        using var reopened = Journal.Open(path, ct);
        var restored = reopened.LoadReview(ct);
        Assert.Equal(recorded, StoreJson.Serialize(restored));
        Assert.Equal(buildings, restored.WorkEvidence!.Buildings);
    }
}
