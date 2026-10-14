using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store.Codec;

internal sealed class AmountConverter : ObjectCodec<Amount>
{
    protected override Amount Read(JsonElement e, Codecs o) => new(e.Get<Resource>("Resource", o), e.I64("Count"));
    protected override void WriteFields(Utf8JsonWriter w, Amount v, Codecs o)
    { w.Put("Resource", v.Resource, o); w.WriteNumber("Count", v.Count); }
}

internal sealed class ProductionAdmissionEvidenceConverter : ObjectCodec<ProductionAdmissionEvidence>
{
    protected override ProductionAdmissionEvidence Read(JsonElement e, Codecs o) => new()
    {
        Stock = e.Get<Fact<EquatableArray<Amount>>>("Stock", o),
        Construction = e.Get<Fact<IReadOnlyDictionary<Resource, long>>>("Construction", o),
        Reservations = e.Get<Fact<EquatableArray<IngredientReservation>>>("Reservations", o),
        Recipes = e.Get<Fact<EquatableArray<ProductionRecipeInput>>>("Recipes", o),
        Promises = e.Get<Fact<EquatableArray<ProductionMaterialPromise>>>("Promises", o),
        Reviewed = e.Get<EquatableArray<ActionId>>("Reviewed", o),
    };
    protected override void WriteFields(Utf8JsonWriter w, ProductionAdmissionEvidence v, Codecs o)
    {
        w.Put("Stock", v.Stock, o); w.Put("Construction", v.Construction, o);
        w.Put("Reservations", v.Reservations, o); w.Put("Recipes", v.Recipes, o);
        w.Put("Promises", v.Promises, o); w.Put("Reviewed", v.Reviewed, o);
    }
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
    { Bill = e.Get<Fact<string>>("Bill", o), Pawn = e.Get<RimGovernor.Brain.Policy.PawnId>("Pawn", o), Items = e.Get<IReadOnlyList<Amount>>("Items", o) };
    protected override void WriteFields(Utf8JsonWriter w, IngredientReservation v, Codecs o)
    { w.Put("Bill", v.Bill, o); w.Put("Pawn", v.Pawn, o); w.Put("Items", v.Items, o); }
}
