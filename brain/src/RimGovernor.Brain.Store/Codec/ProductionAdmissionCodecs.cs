using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store.Codec;

internal sealed class AmountConverter : ObjectCodec<Amount>
{
    protected override Amount Read(JsonElement e, Codecs o) => new(e.Get<Resource>("Resource", o), e.I64("Count"));
    protected override void WriteFields(Utf8JsonWriter w, Amount v, Codecs o)
    { w.Put("Resource", v.Resource, o); w.WriteNum("Count", v.Count); }
}

internal sealed class WorkAdmissionEvidenceConverter : ObjectCodec<WorkAdmissionEvidence>
{
    protected override WorkAdmissionEvidence Read(JsonElement e, Codecs o) => new()
    {
        Tick = e.Get<Tick>("Tick", o),
        Buildings = e.Get<EquatableArray<WorkBuildingCost>>("Buildings", o),
        Standing = e.Has("Standing") ? new(e.Get<EquatableArray<Building>>("Standing", o), e.Get<IReadOnlyDictionary<Resource, long>>("Construction", o)) : null,
        Pawns = e.Get<EquatableArray<WorkPawnAvailability>>("Pawns", o),
        WorkResources = e.Get<EquatableArray<RimGovernor.Brain.GameFacts.Observations.WorkResourcePawn>>("WorkResources", o),
        Stock = e.Get<EquatableArray<Amount>>("Stock", o),
        Reservations = e.Get<EquatableArray<IngredientReservation>>("Reservations", o),
        Recipes = e.Get<EquatableArray<ProductionRecipeInput>>("Recipes", o),
        Promises = e.Get<ProductionPromiseSet>("Promises", o),
        Reviewed = e.Get<EquatableArray<ActionId>>("Reviewed", o),
        NeedTargets = e.Get<EquatableArray<WorkTargetCapacity>>("NeedTargets", o),
    };
    protected override void WriteFields(Utf8JsonWriter w, WorkAdmissionEvidence v, Codecs o)
    {
        w.Put("Tick", v.Tick, o); w.Put("Buildings", v.Buildings, o); if (v.Standing is { } standing) { w.Put("Standing", standing.Buildings, o); w.Put("Construction", standing.Owed, o); } else { w.WriteNull("Standing"); w.WriteNull("Construction"); }
        w.Put("Pawns", v.Pawns, o); w.Put("WorkResources", v.WorkResources, o);
        w.Put("Stock", v.Stock, o);
        w.Put("Reservations", v.Reservations, o); w.Put("Recipes", v.Recipes, o);
        w.Put("Promises", v.Promises, o); w.Put("Reviewed", v.Reviewed, o); w.Put("NeedTargets", v.NeedTargets, o);
    }
}

internal sealed class WorkTargetCapacityConverter : ObjectCodec<WorkTargetCapacity>
{
    protected override WorkTargetCapacity Read(JsonElement e, Codecs o) => new(e.Str("Target"), e.I32("MaxPawns"),
        e.Has("Units") ? new TargetQuantity.Units(e.Dbl("Units")) : new TargetQuantity.Unbounded());
    protected override void WriteFields(Utf8JsonWriter w, WorkTargetCapacity v, Codecs o)
    {
        w.WriteString("Target", v.Target); w.WriteNum("MaxPawns", v.MaxPawns);
        if (v.Quantity is TargetQuantity.Units(var units)) w.WriteNum("Units", units);
    }
}

internal sealed class WorkBuildingCostConverter : ObjectCodec<WorkBuildingCost>
{
    protected override WorkBuildingCost Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.Str("Stuff"), e.Get<EquatableArray<Amount>>("Materials", o))
    { Size = e.Get<Cell>("Size", o) };
    protected override void WriteFields(Utf8JsonWriter w, WorkBuildingCost v, Codecs o)
    { w.WriteString("Definition", v.Definition); w.WriteString("Stuff", v.Stuff); w.Put("Materials", v.Materials, o); w.Put("Size", v.Size, o); }
}

internal sealed class WorkPawnAvailabilityConverter : ObjectCodec<WorkPawnAvailability>
{
    protected override WorkPawnAvailability Read(JsonElement e, Codecs o) => new(e.Str("Pawn"), e.Bool("Available"),
        e.Bool("Applies"), e.Bool("Occupied"), e.Str("Target"))
    { Schedule = e.Get<EquatableArray<string>>("Schedule", o), LocalHour = e.Dbl("LocalHour") };
    protected override void WriteFields(Utf8JsonWriter w, WorkPawnAvailability v, Codecs o)
    { w.WriteString("Pawn", v.Pawn); w.Put("Available", v.Available, o); w.Put("Applies", v.Applies, o); w.WriteBoolean("Occupied", v.Occupied); w.Put("Target", v.Target, o); w.Put("Schedule", v.Schedule, o); w.Put("LocalHour", v.LocalHour, o); }
}

internal sealed class WorkResourcePawnConverter : ObjectCodec<RimGovernor.Brain.GameFacts.Observations.WorkResourcePawn>
{
    protected override RimGovernor.Brain.GameFacts.Observations.WorkResourcePawn Read(JsonElement e, Codecs o) =>
        new(e.Str("Pawn"), e.Str("Assignment"), e.I64("Revision"), e.Str("State"), e.Str("CarriedThing"), e.Str("CarriedResource"), e.I32("CarriedCount")) { LocalHour = e.Dbl("LocalHour"), Method = e.Str("Method"), Reason = e.Str("Reason"), Bill = e.Str("Bill"), Target = e.Str("Target"), Unfinished = e.Str("Unfinished"),
            OrderedWork = e.Has("OrderedWork") ? e.Get<OrderedWorkObservation>("OrderedWork", o) : null };
    protected override void WriteFields(Utf8JsonWriter w, RimGovernor.Brain.GameFacts.Observations.WorkResourcePawn v, Codecs o)
    { w.WriteString("Pawn", v.Pawn); w.WriteString("Assignment", v.Assignment); w.WriteNum("Revision", v.Revision);
      w.WriteString("State", v.State); w.WriteString("CarriedThing", v.CarriedThing); w.WriteString("CarriedResource", v.CarriedResource); w.WriteNum("CarriedCount", v.CarriedCount); w.WriteNum("LocalHour", v.LocalHour); w.WriteString("Method", v.Method); w.WriteString("Reason", v.Reason); w.WriteString("Bill", v.Bill); w.WriteString("Target", v.Target); w.WriteString("Unfinished", v.Unfinished);
      if (v.OrderedWork is { } queue) w.Put("OrderedWork", queue, o); else w.WriteNull("OrderedWork"); }
}

internal sealed class ProductionRecipeInputConverter : ObjectCodec<ProductionRecipeInput>
{
    protected override ProductionRecipeInput Read(JsonElement e, Codecs o) => new(e.Str("Recipe"),
        e.Get<EquatableArray<EquatableArray<Amount>>>("Slots", o), e.Bool("UsesUnfinishedItem"))
        { Products = e.Get<EquatableArray<Amount>>("Products", o), WorkAmount = e.Dbl("WorkAmount"), WorkSpeedStat = e.Str("WorkSpeedStat") };
    protected override void WriteFields(Utf8JsonWriter w, ProductionRecipeInput v, Codecs o)
    { w.WriteString("Recipe", v.Recipe); w.Put("Slots", v.Slots, o); w.WriteBoolean("UsesUnfinishedItem", v.UsesUnfinishedItem);
      w.Put("Products", v.Products, o); w.WriteNum("WorkAmount", v.WorkAmount); w.WriteString("WorkSpeedStat", v.WorkSpeedStat); }
}

internal sealed class ProductionPromiseSetConverter : ObjectCodec<ProductionPromiseSet>
{
    protected override ProductionPromiseSet Read(JsonElement e, Codecs o)
    {
        if (e.Has("Blocked")) return new ProductionPromiseSet.Blocked(Enum.Parse<ProductionPromiseBlock>(e.Str("Blocked")));
        var rows = e.GetProperty("Promises").EnumerateArray().Select(p => new ForecastCommitment(p.Str("Id"), default)
            { MaterialClaims = p.Get<EquatableArray<ForecastMaterialClaim>>("Claims", o) });
        return new ProductionPromiseSet.Assessed(EquatableArray.From(rows));
    }
    protected override void WriteFields(Utf8JsonWriter w, ProductionPromiseSet v, Codecs o)
    {
        if (v is ProductionPromiseSet.Blocked(var reason)) { w.WriteString("Blocked", reason.ToString()); return; }
        var rows = ((ProductionPromiseSet.Assessed)v).Promises;
        w.WriteStartArray("Promises");
        foreach (var row in rows)
        {
            w.WriteStartObject(); w.WriteString("Id", row.Id); w.Put("Claims", row.MaterialClaims, o); w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

internal sealed class ForecastMaterialClaimConverter : ObjectCodec<ForecastMaterialClaim>
{
    protected override ForecastMaterialClaim Read(JsonElement e, Codecs o) => new(e.Get<Resource>("Resource", o),
        ReadUnits(e), e.Str("ReservationBill"));
    private static MaterialUnits ReadUnits(JsonElement e)
    {
        switch (e.Str("Amount"))
        {
            case nameof(MaterialUnits.Exact): return new MaterialUnits.Exact(e.Dbl("Units"));
            case nameof(MaterialUnits.Open): return new MaterialUnits.Open();
        }
        throw new InvalidDataException("invalid material amount state");
    }
    protected override void WriteFields(Utf8JsonWriter w, ForecastMaterialClaim v, Codecs o)
    {
        w.Put("Resource", v.Resource, o);
        if (v.Units is MaterialUnits.Exact(var count)) { w.WriteString("Amount", nameof(MaterialUnits.Exact)); w.WriteNum("Units", count); }
        else w.WriteString("Amount", nameof(MaterialUnits.Open));
        w.WriteString("ReservationBill", v.ReservationBill);
    }
}
internal sealed class IngredientReservationConverter : ObjectCodec<IngredientReservation>
{
    protected override IngredientReservation Read(JsonElement e, Codecs o) => new()
    { Bill = e.Str("Bill"), Observed = new RimGovernor.Brain.GameFacts.Observations.IngredientReservation
        { PawnId = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o).Value,
          Items = EquatableArray.From(e.Get<IReadOnlyList<Amount>>("Items", o).Select(a =>
              new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = a.Resource.Value, Units = a.Count })) } };
    protected override void WriteFields(Utf8JsonWriter w, IngredientReservation v, Codecs o)
    { w.WriteString("Bill", v.Bill); w.Put("Pawn", v.Pawn, o); w.Put("Items", v.Items, o); }
}
