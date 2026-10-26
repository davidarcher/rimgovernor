using RimGovernor.Brain.Executor;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TradeRequestRetirementTests
{
    [Fact]
    public void NativeRetirementOnlyExpiresAnAdvancedRequestWithNoArrivalOrSeller()
    {
        var request = new CommsTradeRequest(TradeRequestKind.Caravan, "faction", "trader", "console", "pawn", 0);
        var view = new ProgressView { Tick = new(100) };
        var read = new CommsTradeInspection { Tick = new(150), RequestKnown = true, LastRequestTick = 100, RequestRetirementTick = 150 };
        Assert.Equal(Effect.Inconclusive, Hands.CommsTradeRequestEffect(request, view, read).Effect);
        Assert.Equal((Effect.Unsuccessful, UnsuccessfulReason.NativeExpired), Hands.CommsTradeRequestEffect(request, view, read with { Tick = new(151) }));
        Assert.Equal(Effect.Completed, Hands.CommsTradeRequestEffect(request, view, read with { Tick = new(151), MatchingArrival = true }).Effect);
        Assert.Equal(Effect.Completed, Hands.CommsTradeRequestEffect(request, view, read with { Tick = new(151), MatchingSeller = true }).Effect);
        Assert.Throws<InvalidOperationException>(() => Hands.CommsTradeRequestEffect(request, view, read with { RequestRetirementTick = null }));
        Assert.Equal(Effect.Inconclusive, Hands.CommsTradeRequestEffect(request, view, read with { LastRequestTick = 0 }).Effect);
    }
}
