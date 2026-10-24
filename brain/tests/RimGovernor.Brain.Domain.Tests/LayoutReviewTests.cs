using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Tools.Commands.ColonyReview;
using Xunit;
using Common = RimGovernor.Contracts.Wire.Common;
using Decision = RimGovernor.Brain.Telemetry.Decision;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// The colony review's layout section (#3751) is compiled in from tools/rg: a synthetic journal (plans), log ring (replan,
// progress and bedroom-step rows) and housing census pin each metric and each threshold's verdict.
public sealed class LayoutReviewTests
{
    private const int Day = 60000;

    [Fact]
    public void ComputeReadsEachMetricPerDayAndOverTheRun()
    {
        var inputs = Healthy();
        var s = LayoutReview.Compute(inputs);

        // A kept replan counts; the skipped one does not.
        Assert.Equal(3, s.Replans);
        Assert.Equal(1.0, s.ReplansPerDay);
        Assert.Equal(0, s.ResitedStarted);
        Assert.Equal(4, s.ResitedUnstarted);
        Assert.Equal(4.0 / 3, s.ResitesPerReplan!.Value, 6);
        Assert.Equal(2, s.MaxOpen);
        Assert.Equal(16.0, s.BedroomArea);
        Assert.Equal(3, s.HousedDay);
        Assert.Equal(1, s.Refusals[Cause.NoSpace.Value]);
        Assert.Equal(1, s.Refusals[Cause.AwaitingPlan.Value]);
        Assert.Equal(0, s.Refusals[Cause.FieldUnavailable.Value]);

        Assert.Equal([1, 2, 3], s.Days.Select(d => d.Day));
        Assert.Equal([2, 1, 0], s.Days.Select(d => d.Replans!.Value));
        Assert.Equal([3, 1, 0], s.Days.Select(d => d.ResitedUnstarted!.Value));
        Assert.Equal(1, s.Days[0].OpenMax);
        Assert.Equal(2, s.Days[1].OpenMax);
        Assert.Null(s.Days[2].OpenMax);
        Assert.Equal(5, s.Days[1].Planned);
        Assert.Equal(2, s.Days[1].Built);
        Assert.Equal(2, s.Days[1].Builders);
        // Three walled rooms (the pen is outdoor, the airlock a child), two distinct links between them.
        Assert.Equal(2.0 / 3, s.Days[0].LinksPerRoom!.Value, 6);
        Assert.Equal(16.0, s.Days[2].BedroomArea);
        Assert.Equal(0, s.Days[0].Housed);
        Assert.Equal(3, s.Days[2].Housed);
        Assert.Equal(3, s.Days[2].Colonists);
        Assert.Equal(1, s.Days[0].Refusals[Cause.NoSpace.Value]);
        Assert.Equal(1, s.Days[1].Refusals[Cause.AwaitingPlan.Value]);
        var plan = Assert.Single(s.Plans);
        Assert.Equal((3, 2), (plan.Rooms, plan.Links));
    }

    [Fact]
    public void ThresholdsPassOnAHealthyRun()
    {
        var inputs = Healthy();
        var t = LayoutReview.Thresholds(LayoutReview.Compute(inputs), inputs);

        Assert.Equal(
            [LayoutReview.StartedResites, LayoutReview.OpenRings, LayoutReview.LinksPerRoom, LayoutReview.HousedBy],
            t.Select(x => x.Name));
        Assert.All(t, x => Assert.Equal(ScoreThreshold.Pass, x.Status));
        Assert.All(t, x => Assert.Null(x.Reason));
        Assert.Equal(3, Find(t, LayoutReview.HousedBy).Value);
        Assert.Equal(LayoutReview.HousedByDay, Find(t, LayoutReview.HousedBy).Bound);
        Assert.False(ColonyScoring.Compute([], t).Failed);
    }

    [Fact]
    public void ThresholdsFailEachWithItsNamedReason()
    {
        var inputs = new LayoutInputs
        {
            Biome = LayoutReview.TemperateBiome,
            Plans = [new PlanRow(1000, Plan([Room(1, PlannedRole.Bedroom, 3, 3), Room(2, PlannedRole.Kitchen, 3, 3)], []))],
            Records =
            [
                Replan(2000, "replanned", (LayoutReplanTiledRules.ReasonDiffSited, true), (LayoutReplanTiledRules.ReasonDiffSited, false)),
                Progress(3000, planned: 4, started: 3, built: 0, open: [1, 2, 3], builders: 0),
            ],
            Housing = [new HousingRow(1000, 3, 1), new HousingRow((7 * Day) + 10, 3, 2)],
        };
        var s = LayoutReview.Compute(inputs);
        var t = LayoutReview.Thresholds(s, inputs);

        Assert.Equal((1, 1), (s.ResitedStarted, s.ResitedUnstarted));
        var started = Find(t, LayoutReview.StartedResites);
        Assert.Equal((ScoreThreshold.Fail, LayoutReview.ReasonStartedResited, 1.0), (started.Status, started.Reason, started.Value));
        var open = Find(t, LayoutReview.OpenRings);
        Assert.Equal((ScoreThreshold.Fail, LayoutReview.ReasonOpenOverBuilders, 3.0, 1.0), (open.Status, open.Reason, open.Value, open.Bound));
        var links = Find(t, LayoutReview.LinksPerRoom);
        Assert.Equal((ScoreThreshold.Fail, LayoutReview.ReasonUnlinked, 0.0, 0.5), (links.Status, links.Reason, links.Value, links.Bound));
        var housed = Find(t, LayoutReview.HousedBy);
        Assert.Equal((ScoreThreshold.Fail, LayoutReview.ReasonNotHoused), (housed.Status, housed.Reason));
        Assert.Null(housed.Value);
        Assert.True(ColonyScoring.Compute([], t).Failed);
    }

    [Fact]
    public void ThresholdsFailAHousedDayPastSix()
    {
        var inputs = new LayoutInputs { Biome = LayoutReview.TemperateBiome, Housing = [new HousingRow(1000, 3, 0), new HousingRow((6 * Day) + 5, 3, 3)] };
        var housed = Find(LayoutReview.Thresholds(LayoutReview.Compute(inputs), inputs), LayoutReview.HousedBy);

        Assert.Equal((ScoreThreshold.Fail, 7.0), (housed.Status, housed.Value));
    }

    [Fact]
    public void ThresholdsAreUnknownWithoutTheirSource()
    {
        var inputs = new LayoutInputs { Biome = "AridShrubland", Housing = [new HousingRow(8 * Day, 3, 0)] };
        var t = LayoutReview.Thresholds(LayoutReview.Compute(inputs), inputs);

        Assert.All(t, x => Assert.Equal(ScoreThreshold.Unknown, x.Status));
        Assert.False(ColonyScoring.Compute([], t).Failed);
    }

    [Fact]
    public void ThresholdsStartedIsUnknownOnRowsWithoutTheAttrAndHousedWhileTheRunEndedEarly()
    {
        var inputs = new LayoutInputs
        {
            Biome = LayoutReview.TemperateBiome,
            Records = [Replan(2000, "replanned", (LayoutReplanTiledRules.ReasonDiffSited, null)), Replan(2500, "skipped", (LayoutReplanTiledRules.ReasonDiffSited, true))],
            Housing = [new HousingRow(4 * Day, 3, 2)],
        };
        var s = LayoutReview.Compute(inputs);
        var t = LayoutReview.Thresholds(s, inputs);

        // The skipped replan is left out; the room without the attr counts unstarted.
        Assert.Equal((1, 0, 1), (s.Replans, s.ResitedStarted, s.ResitedUnstarted));
        Assert.Equal(ScoreThreshold.Unknown, Find(t, LayoutReview.StartedResites).Status);
        Assert.Equal(ScoreThreshold.Unknown, Find(t, LayoutReview.HousedBy).Status);
    }

    [Fact]
    public void CompareCarriesEachThresholdBesideTheBaselines()
    {
        var current = new ColonyScore
        {
            Thresholds = [Threshold(LayoutReview.OpenRings, ScoreThreshold.Fail, 3), Threshold(LayoutReview.HousedBy, ScoreThreshold.Pass, 4)],
        };
        var baseline = new ColonyScore { Thresholds = [Threshold(LayoutReview.OpenRings, ScoreThreshold.Pass, 1)] };
        var delta = ColonyScoring.Compare(current, baseline);

        Assert.NotNull(delta.Thresholds);
        var open = Assert.Single(delta.Thresholds, d => d.Name == LayoutReview.OpenRings);
        Assert.Equal((ScoreThreshold.Fail, ScoreThreshold.Pass, 2.0), (open.Status, open.BaselineStatus, open.ValueDelta));
        var housed = Assert.Single(delta.Thresholds, d => d.Name == LayoutReview.HousedBy);
        Assert.Equal((ScoreThreshold.Pass, ScoreThreshold.Unknown), (housed.Status, housed.BaselineStatus));
        Assert.Null(housed.ValueDelta);
    }

    [Fact]
    public void HousingCountsLiveColonistsOwningABedInAnEnclosedBedroomOrBarracks()
    {
        Observations.RoomState Rooms(string role, string bed, uint openRoof = 0) => new()
        {
            Id = bed + "-room",
            Role = role,
            ProperRoom = true,
            OpenRoofCount = openRoof,
            Beds = EquatableArray.From([new Common.Ref { Id = bed }]),
        };
        Observations.PawnState Pawn(string? bed, bool dead = false) => new() { Dead = dead, OwnedBed = bed is null ? null : new Common.Ref { Id = bed } };
        var rooms = new[]
        {
            Rooms(Wire.Name(RoomRole.Bedroom), "b1"),
            Rooms(Wire.Name(RoomRole.Barracks), "b2", openRoof: 2),
            Rooms(Wire.Name(RoomRole.Barracks), "b4"),
            Rooms(Wire.Name(RoomRole.Kitchen), "b3"),
        };
        var pawns = new[] { Pawn("b1"), Pawn("b2"), Pawn("b3"), Pawn("b4"), Pawn("b1", dead: true), Pawn(null) };

        Assert.Equal((5, 2), ColonyHousing.Count(pawns, rooms));
    }

    // Three days: plans on day 1, replans on days 1 and 2, progress, bedroom refusals and everyone housed on day 3.
    private static LayoutInputs Healthy() => new()
    {
        Biome = LayoutReview.TemperateBiome,
        Plans =
        [
            new PlanRow(1000, Plan(
                [
                    Room(1, PlannedRole.Bedroom, 3, 4),
                    Room(2, PlannedRole.Barracks, 4, 5),
                    Room(3, PlannedRole.Kitchen, 4, 4),
                    Room(4, PlannedRole.Pen, 6, 6) with { Outdoor = true },
                    Room(5, PlannedRole.Airlock, 1, 1) with { Parent = 1 },
                ],
                [new(1, 2, default), new(2, 3, default), new(3, 4, default), new(1, 5, default)])),
        ],
        Records =
        [
            Replan(2000, "replanned", (LayoutReplanTiledRules.ReasonDiffSited, false), (LayoutReplanTiledRules.ReasonDiffSited, false), ("tile_kept_built", false)),
            Replan(3000, "skipped"),
            Replan(4000, "kept", (LayoutReplanTiledRules.ReasonDiffSited, false)),
            Replan(Day + 1000, "replanned", (LayoutReplanTiledRules.ReasonDiffSited, false)),
            Progress(5000, planned: 5, started: 2, built: 1, open: [3], builders: 2),
            Progress(Day + 5000, planned: 5, started: 4, built: 2, open: [3, 4], builders: 2),
            Refusal(4500, Cause.NoSpace.Value, ShelterFamily.Sleeping.Value),
            Refusal(Day + 4500, Cause.AwaitingPlan.Value, ShelterFamily.Sleeping.Value),
            Refusal(4600, Cause.NoSpace.Value, "shelter/heating"),
            Decision(4700, TelemetryKinds.PlannerStep.Name, "admitted", "", new() { ["concern"] = ShelterFamily.Sleeping.Value }),
        ],
        Housing = [new HousingRow(1000, 3, 0), new HousingRow(Day + 1000, 3, 2), new HousingRow((2 * Day) + 1000, 3, 3)],
    };

    private static LayoutPlan Plan(IReadOnlyList<PlannedRoom> rooms, IReadOnlyList<RoomLink> links) => new() { Rooms = rooms, Links = links };

    private static PlannedRoom Room(int id, PlannedRole role, int w, int h) =>
        new() { Id = id, Role = role, Region = RegionRules.RectRegion(new Rectangle(id * 10, 0, w, h)) };

    private static TimelineRecord Replan(int tick, string verdict, params (string Reason, bool? Started)[] rooms) =>
        Decision(tick, TelemetryKinds.LayoutReplanTiled.Name, verdict, "tiled_plan", new()
        {
            ["rooms"] = rooms.Select(r =>
            {
                var row = new Dictionary<string, object?> { ["reason"] = r.Reason };
                if (r.Started is { } started)
                {
                    row["started"] = started;
                }
                return (object?)row;
            }).ToList(),
        });

    private static TimelineRecord Refusal(int tick, string reason, string concern) =>
        Decision(tick, TelemetryKinds.PlannerStep.Name, "refused", reason, new() { ["concern"] = concern });

    private static TimelineRecord Decision(int tick, string kind, string verdict, string reason, Dictionary<string, object?> attrs) => new()
    {
        Kind = kind,
        Context = new Dictionary<string, object?> { ["tick"] = (double)tick },
        Payload = new Dictionary<string, object?>
        {
            [RimGovernor.Brain.Telemetry.Decision.VerdictKey] = verdict,
            [RimGovernor.Brain.Telemetry.Decision.ReasonKey] = reason,
            [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = attrs,
        },
    };

    private static TimelineRecord Progress(int tick, int planned, int started, int built, int[] open, int builders) => new()
    {
        Kind = TelemetryKinds.LayoutProgress.Name,
        Context = new Dictionary<string, object?> { ["tick"] = (double)tick },
        Payload = new Dictionary<string, object?>
        {
            [LayoutProgressTelemetry.PlannedKey] = (double)planned,
            [LayoutProgressTelemetry.StartedKey] = (double)started,
            [LayoutProgressTelemetry.BuiltKey] = (double)built,
            [LayoutProgressTelemetry.OpenKey] = open.Select(o => (object?)(double)o).ToList(),
            [LayoutProgressTelemetry.BuildersKey] = (double)builders,
        },
    };

    private static ScoreThreshold Threshold(string name, string status, double value) => new() { Name = name, Status = status, Value = value };

    private static ScoreThreshold Find(IReadOnlyList<ScoreThreshold> t, string name) => Assert.Single(t, x => x.Name == name);
}
