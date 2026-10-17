using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

public sealed class TradeMissionConverter : ObjectCodec<TradeMission>
{
    protected override TradeMission Read(JsonElement e, Codecs o) =>
        new()
        {
            HomeColony = e.Get<ColonyId>("HomeColony", o),
            HomeMap = e.Get<MapId>("HomeMap", o),
            HomeTile = e.I32("HomeTile"),
            Settlement = e.Str("Settlement"),
            SettlementTile = e.I32("SettlementTile"),
            Crew = e.GetOr("Crew", default(EquatableArray<PawnId>), o),
            Negotiator = e.Get<PawnId>("Negotiator", o),
            SilverBudget = e.I64("SilverBudget"),
            Demand = e.GetOr("Demand", default(EquatableArray<CargoItem>), o),
            Pack = e.GetOr("Pack", default(EquatableArray<CargoItem>), o),
            Phase = e.Get<TradeMissionPhase>("Phase", o),
            ReturnHome = e.BoolOr("ReturnHome"),
            ReturnGoods = e.GetOr("ReturnGoods", default(EquatableArray<CargoItem>), o),
            PurchaseCommitted = e.BoolOr("PurchaseCommitted"),
        };

    protected override void WriteFields(Utf8JsonWriter w, TradeMission v, Codecs o)
    {
        w.Put("HomeColony", v.HomeColony, o);
        w.Put("HomeMap", v.HomeMap, o);
        w.WriteNumber("HomeTile", v.HomeTile);
        w.WriteString("Settlement", v.Settlement);
        w.WriteNumber("SettlementTile", v.SettlementTile);
        w.Put("Crew", v.Crew, o);
        w.Put("Negotiator", v.Negotiator, o);
        w.WriteNumber("SilverBudget", v.SilverBudget);
        w.Put("Demand", v.Demand, o);
        w.Put("Pack", v.Pack, o);
        w.Put("Phase", v.Phase, o);
        w.WriteBoolean("ReturnHome", v.ReturnHome);
        w.Put("ReturnGoods", v.ReturnGoods, o);
        w.WriteBoolean("PurchaseCommitted", v.PurchaseCommitted);
    }
}

