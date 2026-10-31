using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Task 5.5 of <c>native-led-labor-execution</c>: unserved care is observed from two or more native support readings a
/// whole window apart, never fabricated from ticks or from a patient merely needing tend: a reading in which a colonist's job serves
/// the demand closes its window, a rewind reopens it, surgery and bench demands are never tracked, and a missing read observes nothing.</summary>
public sealed class CareStallsTests
{
    private static CoupledForecastRead Read(long tick, params SupportDemand[] demands) =>
        CoupledFixtures.Empty with { Tick = tick, Support = CoupledFixtures.NoSupport with { Demands = EquatableArray.From(demands) } };

    private static SupportDemand Care(bool served = false, string id = "care:patient", string kind = CareStalls.TendKind) =>
        CoupledFixtures.Demand(id, kind, "patient", new(1, 1), 0, .1) with { Served = served };

    private static CareCalibration.Observed Step(ref EquatableArray<UnservedCareWindow> windows, CoupledForecastRead read)
    {
        var observed = Assert.IsType<CareCalibration.Observed>(CareStalls.Observe(windows, read));
        windows = observed.Windows;
        return observed;
    }

    [Fact]
    public void ATendDemandNobodyServesAcrossAWholeWindowIsAStall()
    {
        EquatableArray<UnservedCareWindow> windows = [];
        Assert.Empty(Step(ref windows, Read(100, Care())).Stalls);
        Assert.Equal([new UnservedCareWindow("care:patient", 100)], windows);
        Assert.Empty(Step(ref windows, Read(100 + CareStalls.WindowTicks - 1, Care())).Stalls);
        var observed = Step(ref windows, Read(100 + CareStalls.WindowTicks, Care()));
        Assert.Equal(new CareStall("care:patient", "patient", 100 + CareStalls.WindowTicks), Assert.Single(observed.Stalls));
        Assert.Equal(["care:patient"], observed.Demands);
    }

    [Fact]
    public void AReadingInWhichAColonistServesTheDemandClosesItsWindow()
    {
        EquatableArray<UnservedCareWindow> windows = [];
        Step(ref windows, Read(100, Care()));
        var served = Step(ref windows, Read(100 + CareStalls.WindowTicks / 2, Care(served: true)));
        Assert.Empty(served.Windows);
        Assert.Empty(served.Stalls);
        Assert.Equal(["care:patient"], served.Demands);
        // Unserved again: a fresh window, so a full span must pass again.
        Assert.Empty(Step(ref windows, Read(100 + CareStalls.WindowTicks, Care())).Stalls);
        Assert.Equal(100 + CareStalls.WindowTicks, Assert.Single(windows).Opened);
    }

    [Fact]
    public void AFirstReadingARewindOrADroppedDemandNeverReadsAStall()
    {
        // A single reading is never a stall, however late it is.
        EquatableArray<UnservedCareWindow> windows = [];
        Assert.Empty(Step(ref windows, Read(1_000_000, Care())).Stalls);

        // A reading earlier than the kept window (a rewind or reload) reopens it.
        windows = [new UnservedCareWindow("care:patient", 5000)];
        Assert.Empty(Step(ref windows, Read(100, Care())).Stalls);
        Assert.Equal(100, Assert.Single(windows).Opened);

        // A demand no longer read drops its window: the patient needs no tend now.
        var dropped = Step(ref windows, Read(100 + CareStalls.WindowTicks));
        Assert.Empty(dropped.Windows);
        Assert.Empty(dropped.Demands);
    }

    [Theory]
    [InlineData("surgery")]
    [InlineData("bench")]
    [InlineData("delivery")]
    public void BillDemandsAreNativeBillsAndAreNeverTracked(string kind)
    {
        EquatableArray<UnservedCareWindow> windows = [];
        Step(ref windows, Read(100, Care(id: kind, kind: kind)));
        var observed = Step(ref windows, Read(100 + CareStalls.WindowTicks, Care(id: kind, kind: kind)));
        Assert.Empty(observed.Demands);
        Assert.Empty(observed.Windows);
        Assert.Empty(observed.Stalls);
    }

    [Fact]
    public void AMissingSupportReadObservesNothing() =>
        Assert.Equal(new CareCalibration.Unobserved(CareCalibration.UnobservedReason), CareStalls.Observe([new UnservedCareWindow("care:patient", 0)], null));
}
