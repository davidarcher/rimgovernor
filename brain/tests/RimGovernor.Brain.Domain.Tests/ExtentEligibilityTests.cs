using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The extent eligibility overlay names its evidence: the facility census and the hostile presence are read or unread, never absent.</summary>
public sealed class ExtentEligibilityTests
{
    private static readonly ColonyExtent OneFacility = new()
    {
        Regions =
        [
            new ExtentRegion
            {
                Cells = [new ExtentCell { Cell = new Cell(1, 1), Provenance = [new ExtentProvenance { Origin = ExtentOrigin.Facility, Facility = "Thing_Bed1" }] }],
            },
        ],
    };

    private static IReadOnlyList<string> Holds(FacilityCensus facilities, HostilePresence threat) =>
        Assert.Single(ExtentEligibilityRules.ExtentEligibility(new ExtentEligibilityRequest { Extent = OneFacility, Facilities = facilities, Threat = threat }).Regions).HoldReasons;

    [Fact]
    public void ACountedCensusAndAbsentHostilesHoldOnlyTheRouteCensusThatDoesNotExist()
    {
        Assert.Equal(["route_unknown"], Holds(new FacilityCensus.Counted(["Thing_Bed1"]), HostilePresence.Absent));
    }

    [Fact]
    public void ALostFacilityHolds()
    {
        Assert.Equal(["facility_lost", "route_unknown"], Holds(new FacilityCensus.Counted(["Thing_Other"]), HostilePresence.Absent));
    }

    [Fact]
    public void AnUnreadCensusAndUnreadHostilesAreHolds()
    {
        Assert.Equal(["threat_unknown", "facilities_unknown", "route_unknown"], Holds(new FacilityCensus.Unread(), HostilePresence.Unread));
    }

    [Fact]
    public void PresentHostilesHoldEveryRegion()
    {
        Assert.Equal(["threat_present", "route_unknown"], Holds(new FacilityCensus.Counted(["Thing_Bed1"]), HostilePresence.Present));
    }

    [Fact]
    public void NoHistoryIsTheNamedView()
    {
        Assert.False(ExtentEligibilityView.NoHistory.Known);
        Assert.Equal("extent_unknown", ExtentEligibilityView.NoHistory.Reason);
    }
}
