using System.Globalization;
using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3449: the in-process (Mono) runtime formats doubles at 15 significant digits, so a stored NeedBenefit never equalled the
/// recomputed one. The store writes floating-point numbers as round-tripping text itself, whatever the runtime default is.
/// </summary>
public sealed class StoreDoubleRoundTripTests
{
    [Theory]
    [InlineData("0.4677710719406605")]
    [InlineData("0.0322289280593395")]
    [InlineData("0.30000000000000004")]
    [InlineData("1E+16")]
    public void DoubleIsWrittenWithEveryDigitItNeeds(string text)
    {
        var value = double.Parse(text, CultureInfo.InvariantCulture);
        var written = Write(w => w.WriteNum("v", value));
        var back = JsonDocument.Parse(written).RootElement.GetProperty("v").GetDouble();
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(back));
    }

    [Fact]
    public void RecomputedNeedBenefitEqualsTheStoredOne()
    {
        var recomputed = 0.5 - 0.0322289280593395;
        Assert.Equal(recomputed, StoreJson.Deserialize<double>(StoreJson.Serialize(recomputed)));
    }

    [Fact]
    public void FloatRoundTripsBitIdentical()
    {
        var value = 0.1f + 0.2f;
        var back = StoreJson.Deserialize<float>(StoreJson.Serialize(value));
        Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(back));
    }

    [Fact]
    public void NonFiniteStillRefused() =>
        Assert.Throws<ArgumentException>(() => Write(w => w.WriteNum("v", double.NaN)));

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
