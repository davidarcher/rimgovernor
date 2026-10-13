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

public sealed class StandardConverter : ObjectCodec<Standard>
{
    protected override Standard Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Get<StandardId>("ID", o),
            Kind = e.Get<ConcernId>("Kind", o),
            Priority = e.I32("Priority"),
            Snapshot = e.Get<GenerationSnapshot>("Snapshot", o),
            Episode = e.U64("Episode"),
            Status = e.Get<StandardStatus>("Status", o),
            Finding = e.Get<Finding>("Finding", o),
            RecoveryObserved = e.BoolOr("RecoveryObserved"),
            Once = e.BoolOr("Once"),
            Mission = e.GetOr<TradeMission?>("Mission", null, o),
            Record = e.StrOr("Record") is { Length: > 0 } record ? record : null,
        };

    protected override void WriteFields(Utf8JsonWriter w, Standard v, Codecs o)
    {
        w.Put("ID", v.Id, o);
        w.Put("Kind", v.Kind, o);
        w.WriteNumber("Priority", v.Priority);
        w.Put("Snapshot", v.Snapshot, o);
        w.WriteNumber("Episode", v.Episode);
        w.Put("Status", v.Status, o);
        w.Put("Finding", v.Finding, o);
        w.WriteBoolean("RecoveryObserved", v.RecoveryObserved);
        w.WriteBoolean("Once", v.Once);
        if (v.Mission is { } mission)
        {
            w.Put("Mission", mission, o);
        }
        if (!string.IsNullOrEmpty(v.Record))
        {
            w.WriteString("Record", v.Record);
        }
    }
}

public sealed class IncidentConverter : ObjectCodec<Incident>
{
    protected override Incident Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Get<IncidentId>("ID", o),
            Kind = e.Get<ConcernId>("Kind", o),
            Subject = e.GetOr("Subject", default(PawnId), o),
            Trigger = e.Str("Trigger"),
            Priority = e.I32("Priority"),
            Snapshot = e.Get<GenerationSnapshot>("Snapshot", o),
            Started = e.Get<Tick>("Started", o),
            Closed = e.BoolOr("Closed"),
            Ended = e.GetOr("Ended", default(Tick), o),
            HuntPrey = e.GetOr("HuntPrey", default(EquatableArray<PawnId>), o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Incident v, Codecs o)
    {
        w.Put("ID", v.Id, o);
        w.Put("Kind", v.Kind, o);
        w.Put("Subject", v.Subject, o);
        w.WriteString("Trigger", v.Trigger);
        w.WriteNumber("Priority", v.Priority);
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Started", v.Started, o);
        w.WriteBoolean("Closed", v.Closed);
        w.Put("Ended", v.Ended, o);
        if (v.HuntPrey.Count > 0)
        {
            w.Put("HuntPrey", v.HuntPrey, o);
        }
    }
}
