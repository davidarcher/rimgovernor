using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A pawn row's needs and social blocks are total (#3768): every member is <c>required</c>, a need the pawn lacks is
/// <see cref="NeedLevel.Lacks"/> (not an absent number), and the psylink level is read for every pawn. On the wire both
/// round-trip through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class PawnNeedsTests
{
    /// <summary>A pawn with no needs at all (a mechanoid without food, rest, mood or joy): the needs block tests start from.</summary>
    internal static readonly PawnNeeds NoNeeds = new()
    {
        Food = new FoodNeed.Lacks(),
        Rest = new NeedLevel.Lacks(),
        Mood = new NeedLevel.Lacks(),
        Joy = new NeedLevel.Lacks(),
        BreakThresholdMinor = 0.35,
        PsylinkLevel = 0,
    };

    [Fact]
    public void NeedsJsonRoundTripIsExact()
    {
        var needs = NoNeeds with { Food = new FoodNeed.Has(0.25, RimGovernor.Brain.GameFacts.Defs.HungerCategory.Hungry), Mood = new NeedLevel.Has(0.5), PsylinkLevel = 2 };

        var json = OrdersJson.Serialize(needs);

        Assert.Equal(
            """{"food":{"type":"has","level":0.25,"hunger":"HUNGER_CATEGORY_HUNGRY"},"rest":{"type":"lacks"},"mood":{"type":"has","level":0.5},"joy":{"type":"lacks"},"breakThresholdMinor":0.35,"psylinkLevel":2}""",
            json);
        Assert.Equal(needs, OrdersJson.Deserialize<PawnNeeds>(json));
    }

    [Fact]
    public void SocialJsonRoundTripIsExact()
    {
        var social = new PawnSocial
        {
            HighExpectations = true,
            ExpectationDefName = "High",
            Memories = [new Thought { DefName = "AteWithoutTable", MoodOffsetTotal = -3 }],
            Situational = [],
            SituationalCacheStale = false,
        };

        var json = OrdersJson.Serialize(social);

        Assert.Equal(
            """{"highExpectations":true,"expectationDefName":"High","memories":[{"defName":"AteWithoutTable","moodOffsetTotal":-3}],"situational":[],"situationalCacheStale":false}""",
            json);
        Assert.Equal(social, OrdersJson.Deserialize<PawnSocial>(json));
    }
}
