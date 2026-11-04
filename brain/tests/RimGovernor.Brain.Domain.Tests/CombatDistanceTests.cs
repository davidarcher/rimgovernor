using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared combat distance (#4017): cells at the far ends of the int domain are as far apart as they are, not one
/// wrapped cell; the same distance reads in both directions; and the mortar safety check that reads it refuses a
/// target whose only "nearby" colonist is a wrapped difference away.
/// </summary>
public sealed class CombatDistanceTests
{
    [Fact]
    public void ExtremeCoordinatesReportTheirTrueSeparation()
    {
        var a = new Cell(int.MaxValue, int.MaxValue);
        var b = new Cell(int.MinValue, int.MinValue);

        var d = CombatMortarRules.Dist(a, b);

        var span = (double)int.MaxValue - int.MinValue;
        Assert.Equal(Math.Sqrt(2 * span * span), d, 1e-3);
        Assert.True(d > CombatMortarRules.MortarSafeRadius);
    }

    [Fact]
    public void DistanceIsSymmetric()
    {
        var a = new Cell(3, -7);
        var b = new Cell(int.MaxValue, 11);

        Assert.Equal(CombatMortarRules.Dist(a, b), CombatMortarRules.Dist(b, a));
        Assert.Equal(5.0, CombatMortarRules.Dist(new Cell(0, 0), new Cell(3, 4)));
    }

    [Fact]
    public void AWrappedDifferenceDoesNotPutATargetNearOurs()
    {
        var ours = new[] { new Cell(int.MinValue, int.MinValue) };

        Assert.False(CombatMortarRules.NearAny(ours, new Cell(int.MaxValue, int.MaxValue)));
        Assert.True(CombatMortarRules.NearAny(ours, new Cell(int.MinValue + 3, int.MinValue + 4)));
    }
}
