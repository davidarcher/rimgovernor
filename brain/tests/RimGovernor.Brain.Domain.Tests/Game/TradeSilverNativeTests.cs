using System.Reflection;
using RimGovernor.Native;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

[CollectionDefinition("Native trade definitions", DisableParallelization = true)]
public sealed class NativeTradeDefinitionsFixture;

[Collection("Native trade definitions")]
public sealed class TradeSilverNativeTests
{
#pragma warning disable CA1852 // DispatchProxy derives its implementation from this class.
    private class TraderSet : DispatchProxy
    {
        internal Pawn? Negotiator { get; private set; }
        internal Thing[] Items { get; set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ITrader.ColonyThingsWillingToBuy))
                throw new NotSupportedException(targetMethod?.Name);
            Negotiator = (Pawn)args![0]!;
            return Items;
        }
    }
#pragma warning restore CA1852

    [Fact]
    public void CurrencyComesOnlyFromTheActualTradersNativeSetForItsNegotiator()
    {
        var previous = ThingDefOf.Silver;
        try
        {
            ThingDefOf.Silver = new ThingDef { defName = nameof(ThingDefOf.Silver) };
            var trader = DispatchProxy.Create<ITrader, TraderSet>();
            var source = (TraderSet)(object)trader;
            var negotiator = new Pawn();
            // The native set already filters fog, Home/storage and this trader's reachability,
            // and includes reachable haul-source contents. Unlisted map silver is never counted.
            source.Items = [new Thing { def = ThingDefOf.Silver, stackCount = 70 },
                new Thing { def = new ThingDef(), stackCount = 900 }, new Thing { def = ThingDefOf.Silver, stackCount = 11 }];
            Assert.Equal(81, NativeTradeSilver.ForTrader(trader, negotiator));
            Assert.Same(negotiator, source.Negotiator);
            source.Items = [];
            Assert.Equal(0, NativeTradeSilver.ForTrader(trader, negotiator));
        }
        finally { ThingDefOf.Silver = previous; }
    }
}
