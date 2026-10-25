using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Operations;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RescueBed.Search), "search")]
[JsonDerivedType(typeof(RescueBed.At), "at")]
public abstract record RescueBed
{
    private protected RescueBed() { }
    public sealed record Search : RescueBed;
    public sealed record At([property: JsonRequired] Cell Cell) : RescueBed;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MortarAim.Cleared), "cleared")]
[JsonDerivedType(typeof(MortarAim.At), "at")]
public abstract record MortarAim
{
    private protected MortarAim() { }
    public sealed record Cleared : MortarAim;
    public sealed record At([property: JsonRequired] Cell Cell) : MortarAim;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DesignationLocation.ByTarget), "by_target")]
[JsonDerivedType(typeof(DesignationLocation.At), "at")]
public abstract record DesignationLocation
{
    private protected DesignationLocation() { }
    public sealed record ByTarget : DesignationLocation;
    public sealed record At([property: JsonRequired] Cell Cell) : DesignationLocation;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RitualLocation.NotStarting), "not_starting")]
[JsonDerivedType(typeof(RitualLocation.At), "at")]
public abstract record RitualLocation
{
    private protected RitualLocation() { }
    public sealed record NotStarting : RitualLocation;
    public sealed record At([property: JsonRequired] Cell Cell) : RitualLocation;
}