using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Operations;
using RimGovernor.Contracts.Wire.Receipts;
using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;
using WireHostility = RimGovernor.Contracts.Wire.Operations.HostilityResponse;
using WireMedicalCare = RimGovernor.Contracts.Wire.Operations.MedicalCare;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go wire_enums.go: pawn-setting, skill, waste, bill, combat, route and quest enums on the wire map onto the
// vanilla names the domain and policy layers use; an unspecified or unknown value maps to "" (null for a domain
// enum with no empty member). Go's name maps are switches here; the domain enums carry their wire names.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>medicalCareWire</c> inverted: the domain care tier of a wire tier, null when unspecified or unknown.</summary>
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

    /// <summary>Go's <c>MedicalCareName</c>: the MedicalCareCategory name of a wire care tier.</summary>
    public static string MedicalCareName(WireMedicalCare care) => MedicalCareOf(care) is { } tier ? Domain.Wire.Name(tier) : "";

    /// <summary>Go's <c>HostilityName</c>: the HostilityResponseMode of a wire hostility response, null when unspecified or unknown.</summary>
    public static DomainHostility? HostilityName(WireHostility value) =>
        value switch
        {
            WireHostility.Ignore => DomainHostility.Ignore,
            WireHostility.Attack => DomainHostility.Attack,
            WireHostility.Flee => DomainHostility.Flee,
            _ => null,
        };

    /// <summary>Go's <c>PassionName</c>: the vanilla Passion name ("None", "Minor", "Major").</summary>
    public static string PassionName(Passion passion) =>
        passion switch
        {
            Passion.None => "None",
            Passion.Minor => "Minor",
            Passion.Major => "Major",
            _ => "",
        };

    /// <summary>Go's <c>WasteKindName</c>: policy's waste kind ("corpse", "spoiled").</summary>
    public static string WasteKindName(WasteKind kind) =>
        kind switch
        {
            WasteKind.Corpse => "corpse",
            WasteKind.Spoiled => "spoiled",
            _ => "",
        };

    /// <summary>Go's <c>RepeatModeName</c>: the vanilla BillRepeatModeDef name of a repeat mode.</summary>
    public static string RepeatModeName(RepeatMode mode) =>
        mode switch
        {
            RepeatMode.Forever => "Forever",
            RepeatMode.Count => "RepeatCount",
            RepeatMode.Target => "TargetCount",
            _ => "",
        };

    /// <summary>Go's <c>FireModeName</c>: policy's fire mode ("fire_at_will", "hold_fire").</summary>
    public static string FireModeName(CombatFireMode mode) =>
        mode switch
        {
            CombatFireMode.AtWill => "fire_at_will",
            CombatFireMode.Hold => "hold_fire",
            _ => "",
        };

    /// <summary>Go's <c>RouteKindName</c>: policy's route facility kind ("bed", "bench", ...).</summary>
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

    /// <summary>Go's <c>QuestStatusName</c>: the vanilla QuestState name ("NotYetAccepted", ...).</summary>
    public static string QuestStatusName(QuestStatus status) =>
        status switch
        {
            QuestStatus.NotYetAccepted => "NotYetAccepted",
            QuestStatus.Ongoing => "Ongoing",
            QuestStatus.EndedUnknownOutcome => "EndedUnknownOutcome",
            QuestStatus.EndedSuccess => "EndedSuccess",
            QuestStatus.EndedFailed => "EndedFailed",
            QuestStatus.EndedOfferExpired => "EndedOfferExpired",
            QuestStatus.EndedInvalid => "EndedInvalid",
            _ => "",
        };
}
