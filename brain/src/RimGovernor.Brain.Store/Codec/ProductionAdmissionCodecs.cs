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
        Buildings = e.Get<Fact<EquatableArray<WorkBuildingCost>>>("Buildings", o),
        Standing = e.Get<Fact<EquatableArray<Building>>>("Standing", o),
        Pawns = e.Get<Fact<EquatableArray<WorkPawnAvailability>>>("Pawns", o),
        WorkResources = e.Get<Fact<EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>>>("WorkResources", o),
        Stock = e.Get<Fact<EquatableArray<Amount>>>("Stock", o),
        Construction = e.Get<Fact<IReadOnlyDictionary<Resource, long>>>("Construction", o),
        Reservations = e.Get<Fact<EquatableArray<IngredientReservation>>>("Reservations", o),
        Recipes = e.Get<Fact<EquatableArray<ProductionRecipeInput>>>("Recipes", o),
        Promises = e.Get<Fact<EquatableArray<ProductionMaterialPromise>>>("Promises", o),
        Reviewed = e.Get<EquatableArray<ActionId>>("Reviewed", o),
        NeedTargets = e.Get<EquatableArray<WorkTargetCapacity>>("NeedTargets", o),
    };
    protected override void WriteFields(Utf8JsonWriter w, WorkAdmissionEvidence v, Codecs o)
    {
        w.Put("Tick", v.Tick, o); w.Put("Buildings", v.Buildings, o); w.Put("Standing", v.Standing, o);
        w.Put("Pawns", v.Pawns, o); w.Put("WorkResources", v.WorkResources, o);
        w.Put("Stock", v.Stock, o); w.Put("Construction", v.Construction, o);
        w.Put("Reservations", v.Reservations, o); w.Put("Recipes", v.Recipes, o);
        w.Put("Promises", v.Promises, o); w.Put("Reviewed", v.Reviewed, o); w.Put("NeedTargets", v.NeedTargets, o);
    }
}

internal sealed class WorkTargetCapacityConverter : ObjectCodec<WorkTargetCapacity>
{
    protected override WorkTargetCapacity Read(JsonElement e, Codecs o) => new(e.Str("Target"), e.Get<Fact<int>>("MaxPawns", o), e.Get<Fact<double>>("Quantity", o));
    protected override void WriteFields(Utf8JsonWriter w, WorkTargetCapacity v, Codecs o)
    { w.WriteString("Target", v.Target); w.Put("MaxPawns", v.MaxPawns, o); w.Put("Quantity", v.Quantity, o); }
}

internal sealed class WorkBuildingCostConverter : ObjectCodec<WorkBuildingCost>
{
    protected override WorkBuildingCost Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.Str("Stuff"), e.Get<Fact<EquatableArray<Amount>>>("Materials", o))
    { Size = e.GetOr<Fact<Cell>>("Size", default, o) };
    protected override void WriteFields(Utf8JsonWriter w, WorkBuildingCost v, Codecs o)
    { w.WriteString("Definition", v.Definition); w.WriteString("Stuff", v.Stuff); w.Put("Materials", v.Materials, o); if (v.Size.IsKnown) w.Put("Size", v.Size, o); }
}

internal sealed class WorkPawnAvailabilityConverter : ObjectCodec<WorkPawnAvailability>
{
    protected override WorkPawnAvailability Read(JsonElement e, Codecs o) => new(e.Str("Pawn"), e.Get<Fact<bool>>("Available", o),
        e.Get<Fact<bool>>("Applies", o), e.Get<Fact<bool>>("Occupied", o), e.Get<Fact<string>>("Target", o))
    { Schedule = e.Get<Fact<EquatableArray<string>>>("Schedule", o), LocalHour = e.Get<Fact<double>>("LocalHour", o) };
    protected override void WriteFields(Utf8JsonWriter w, WorkPawnAvailability v, Codecs o)
    { w.WriteString("Pawn", v.Pawn); w.Put("Available", v.Available, o); w.Put("Applies", v.Applies, o); w.Put("Occupied", v.Occupied, o); w.Put("Target", v.Target, o); w.Put("Schedule", v.Schedule, o); w.Put("LocalHour", v.LocalHour, o); }
}

internal sealed class WorkResourcePawnConverter : ObjectCodec<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>
{
    protected override RimGovernor.Contracts.Wire.Observations.WorkResourcePawn Read(JsonElement e, Codecs o) =>
        new(e.Str("Pawn"), e.Str("Assignment"), e.I64("Revision"), e.Str("State"), e.Str("CarriedThing"), e.Str("CarriedResource"), e.I32("CarriedCount")) { LocalHour = e.Get<double?>("LocalHour", o) ?? throw new InvalidDataException(), Bill = e.Str("Bill"), Target = e.Str("Target") };
    protected override void WriteFields(Utf8JsonWriter w, RimGovernor.Contracts.Wire.Observations.WorkResourcePawn v, Codecs o)
    { w.WriteString("Pawn", v.Pawn); w.WriteString("Assignment", v.Assignment); w.WriteNum("Revision", v.Revision);
      w.WriteString("State", v.State); w.WriteString("CarriedThing", v.CarriedThing); w.WriteString("CarriedResource", v.CarriedResource); w.WriteNum("CarriedCount", v.CarriedCount); w.Put<double?>("LocalHour", v.LocalHour, o); w.WriteString("Bill", v.Bill); w.WriteString("Target", v.Target); }
}

internal sealed class ProductionRecipeInputConverter : ObjectCodec<ProductionRecipeInput>
{
    protected override ProductionRecipeInput Read(JsonElement e, Codecs o) => new(e.Str("Recipe"),
        e.Get<Fact<EquatableArray<EquatableArray<Amount>>>>("Slots", o), e.Get<Fact<bool>>("UsesUnfinishedItem", o));
    protected override void WriteFields(Utf8JsonWriter w, ProductionRecipeInput v, Codecs o)
    { w.WriteString("Recipe", v.Recipe); w.Put("Slots", v.Slots, o); w.Put("UsesUnfinishedItem", v.UsesUnfinishedItem, o); }
}

internal sealed class ProductionMaterialPromiseConverter : ObjectCodec<ProductionMaterialPromise>
{
    protected override ProductionMaterialPromise Read(JsonElement e, Codecs o) => new(e.Str("Id"), e.Get<EquatableArray<ForecastMaterialClaim>>("Claims", o));
    protected override void WriteFields(Utf8JsonWriter w, ProductionMaterialPromise v, Codecs o)
    { w.WriteString("Id", v.Id); w.Put("Claims", v.Claims, o); }
}

internal sealed class ForecastMaterialClaimConverter : ObjectCodec<ForecastMaterialClaim>
{
    protected override ForecastMaterialClaim Read(JsonElement e, Codecs o) => new(e.Get<Resource>("Resource", o),
        e.Get<Fact<double>>("Units", o), e.Get<Fact<string>>("ReservationBill", o));
    protected override void WriteFields(Utf8JsonWriter w, ForecastMaterialClaim v, Codecs o)
    { w.Put("Resource", v.Resource, o); w.Put("Units", v.Units, o); w.Put("ReservationBill", v.ReservationBill, o); }
}

internal sealed class IngredientReservationConverter : ObjectCodec<IngredientReservation>
{
    protected override IngredientReservation Read(JsonElement e, Codecs o) => new()
    { Bill = e.Get<Fact<string>>("Bill", o), Pawn = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o), Items = e.Get<IReadOnlyList<Amount>>("Items", o) };
    protected override void WriteFields(Utf8JsonWriter w, IngredientReservation v, Codecs o)
    { w.Put("Bill", v.Bill, o); w.Put("Pawn", v.Pawn, o); w.Put("Items", v.Items, o); }
}
