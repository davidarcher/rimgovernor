using RimGovernor.Native;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class IncidentCategoryProbabilityTests
{
    [Fact]
    public void NativePointRangeKeepsInclusiveAndStrictThresholdAtomsSeparate()
    {
        var range = new Verse.FloatRange(0.5f, 1.5f);
        var inclusive = IncidentCategoryReader.First(value => IncidentCategoryReader.Points(200, range, value) >= 200);
        var strict = IncidentCategoryReader.First(value => IncidentCategoryReader.Points(200, range, value) > 200);
        Assert.True(strict > inclusive);
        Assert.Equal(200, IncidentCategoryReader.Points(200, range, inclusive));
        Assert.True(IncidentCategoryReader.Points(200, range, inclusive - 1) < 200);
        Assert.Equal(200, IncidentCategoryReader.Points(200, range, strict - 1));
        Assert.True(IncidentCategoryReader.Points(200, range, strict) > 200);
        Assert.Equal(-1, IncidentCategoryReader.Points(-1, range, uint.MaxValue));
    }
    [Fact]
    public void RoundedNativeUniformHasAnAtomAtHalfInsteadOfAContinuousUniformLaw()
    {
        Assert.Equal(2147483584.0 / 4294967296.0, IncidentCategoryReader.Chance(0.5));
        Assert.Equal(0, IncidentCategoryReader.Chance(0));
        Assert.Equal(1, IncidentCategoryReader.Chance(1));
    }

    [Fact]
    public void NativeMtbRollRetainsTheRoundedOneEndpointAndTheSmallProbabilityGate()
    {
        Assert.Equal(1, IncidentCategoryReader.MtbChance(0));
        Assert.Equal(0, IncidentCategoryReader.MtbChance(float.PositiveInfinity));
        // Rand.MTBEventOccurs scales this tiny probability by 8^5. Its first Value roll admits
        // <= 1/32768 (131073 uints); the second admits < 32768/60000000 (2345625 uints).
        // Rounded floats and the inclusive first gate make the product slightly larger than 1/60000000.
        const double values = 4294967296;
        Assert.True((float)(131072.0 / uint.MaxValue) <= 1.0 / 32768);
        Assert.True((float)(131073.0 / uint.MaxValue) > 1.0 / 32768);
        Assert.True((float)(2345624.0 / uint.MaxValue) < 32768.0 / 60000000);
        Assert.True((float)(2345625.0 / uint.MaxValue) >= 32768.0 / 60000000);
        Assert.Equal((131073 / values) * (2345625 / values), IncidentCategoryReader.MtbChance(1000000));
        Assert.True(IncidentCategoryReader.MtbChance(1f / 60) < 1);
    }
}
