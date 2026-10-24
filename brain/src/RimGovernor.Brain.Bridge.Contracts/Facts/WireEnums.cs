using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Operations;
using RimGovernor.Contracts.Wire.Receipts;
using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;
using WireHostility = RimGovernor.Contracts.Wire.Operations.HostilityResponse;
using WireMedicalCare = RimGovernor.Contracts.Wire.Operations.MedicalCare;

namespace RimGovernor.Brain.Bridge.Contracts;

// Pawn-setting, skill, waste, bill, combat, route and quest enums on the wire map onto the
// vanilla names the domain and policy layers use; an unspecified or unknown value maps to "" (null for a domain
// enum with no empty member). The name maps are switches; the domain enums carry their wire names.
public static class WireEnums
{
    /// <summary>The domain care tier of a wire tier, null when unspecified or unknown.</summary>
    internal static DomainMedicalCare? MedicalCareOf(WireMedicalCare care) =>
        care switch
        {
            WireMedicalCare.NoCare => DomainMedicalCare.None,
            WireMedicalCare.NoMedicine => DomainMedicalCare.NoMeds,
            WireMedicalCare.HerbalOrWorse => DomainMedicalCare.Herbal,
            WireMedicalCare.NormalOrWorse => DomainMedicalCare.Normal,
            WireMedicalCare.Best => DomainMedicalCare.Best,
            _ => null,
        };

    /// <summary>The domain care tier of a wire tier; native always names one, so an unspecified tier throws.</summary>
    public static DomainMedicalCare RequiredMedicalCare(WireMedicalCare care) =>
        MedicalCareOf(care) ?? throw new BridgeContractException($"medical care {care} names no care tier");

    /// <summary>The MedicalCareCategory name of a wire care tier.</summary>
    public static string MedicalCareName(WireMedicalCare care) => MedicalCareOf(care) is { } tier ? Domain.Wire.Name(tier) : "";

    /// <summary>The HostilityResponseMode of a wire hostility response, null when unspecified or unknown.</summary>
    public static DomainHostility? HostilityName(WireHostility value) =>
        value switch
        {
            WireHostility.Ignore => DomainHostility.Ignore,
            WireHostility.Attack => DomainHostility.Attack,
            WireHostility.Flee => DomainHostility.Flee,
            _ => null,
        };

    /// <summary>The vanilla Passion name ("None", "Minor", "Major").</summary>
    public static string PassionName(Passion passion) =>
        passion switch
        {
            Passion.None => "None",
            Passion.Minor => "Minor",
            Passion.Major => "Major",
            _ => "",
        };


    /// <summary>Policy's route facility kind ("bed", "bench", ...).</summary>
    public static string RouteKindName(RouteFacilityKind kind) =>
        kind switch
        {
            RouteFacilityKind.Bed => "bed",
            RouteFacilityKind.Bench => "bench",
            RouteFacilityKind.Storage => "storage",
            RouteFacilityKind.Dining => "dining",
            RouteFacilityKind.Defense => "defense",
            RouteFacilityKind.Stockpile => "stockpile",
            _ => "",
        };
}
