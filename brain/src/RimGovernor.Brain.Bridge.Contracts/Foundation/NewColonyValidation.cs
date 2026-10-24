using System.Text.RegularExpressions;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>Bounds a <see cref="NewColonyRequest"/> must respect; the HTTP route and native share them.</summary>
public static class NewColonyBounds
{
    public const int MaxColonists = 10;
    public const int MinMapSize = 100;
    public const int MaxMapSize = 400;
    public const double MinCoverage = 0.05;
    public const double MaxCoverage = 1.0;
    public const int MinTimeoutMs = 1000;
    public const int MaxTimeoutMs = 1_800_000;
    public const int MaxTemperature = 200;
    public const int MaxBiomes = 32;
    public const int MaxPartsPerKind = 64;
    public const int MaxThingCount = 100_000;
    public const int MaxTraitDegree = 10;
    public const double MaxSeverity = 1;

    /// <summary>The fixed <c>player_direction</c> the controller sends on lifecycle save and load; the field must be nonzero and carries no other meaning.</summary>
    public const ulong LifecycleDirection = 1;
}

public static class NewColonyValidation
{
    private static readonly Regex NewColonySaveName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Checks a request against every bound the wire contract names, before it reaches native.</summary>
    public static void ValidateNewColonyRequest(NewColonyRequest? request)
    {
        if (request is null)
        {
            throw BridgeContractException.Contract("new colony request required");
        }

        if (request.RequestId is null || !WireValidation.IsValidId((request.RequestId ?? "")))
        {
            throw BridgeContractException.Contract("new colony request requires a request id");
        }

        if (request.TimeoutMs < NewColonyBounds.MinTimeoutMs || request.TimeoutMs > NewColonyBounds.MaxTimeoutMs)
        {
            throw BridgeContractException.Contract("new colony timeout out of range");
        }

        ValidateNewColonySpec(request.Spec);
    }

    /// <summary>The spec's presence and range rules. Whether a defName exists is native's call.</summary>
    public static void ValidateNewColonySpec(NewColonySpec? spec)
    {
        if (spec is null)
        {
            throw BridgeContractException.Contract("new colony spec required");
        }

        RequireId(spec.Scenario is not null, (spec.Scenario ?? ""), "scenario");
        RequireId(spec.Seed is not null, (spec.Seed ?? ""), "seed");
        RequireId(spec.Difficulty is not null, (spec.Difficulty ?? ""), "difficulty");
        RequireId(spec.Storyteller is not null, (spec.Storyteller ?? ""), "storyteller");
        if (spec.Theme is { Length: > 0 } theme && (!WireValidation.IsValidId(theme) || spec.Ideoligion is not null))
        {
            throw BridgeContractException.Contract("new colony theme invalid: a theme id designs the ideoligion, so never with an explicit one");
        }

        if (spec.WorldTemperature is not null && !WireValidation.IsValidId((spec.WorldTemperature ?? "")))
        {
            throw BridgeContractException.Contract("new colony world temperature invalid");
        }

        if (spec.ColonistCount < 1 || spec.ColonistCount > NewColonyBounds.MaxColonists)
        {
            throw BridgeContractException.Contract("new colony colonist count out of range");
        }

        if (spec.Biomes.Count > NewColonyBounds.MaxBiomes || !ValidIds(spec.Biomes))
        {
            throw BridgeContractException.Contract("new colony biomes invalid");
        }

        if (spec.MapSize < NewColonyBounds.MinMapSize || spec.MapSize > NewColonyBounds.MaxMapSize)
        {
            throw BridgeContractException.Contract("new colony map size out of range");
        }

        if (double.IsNaN(spec.PlanetCoverage) || spec.PlanetCoverage < NewColonyBounds.MinCoverage || spec.PlanetCoverage > NewColonyBounds.MaxCoverage)
        {
            throw BridgeContractException.Contract("new colony planet coverage out of range");
        }

        foreach (var (name, limit) in new[] { ("min", spec.MinTemperature), ("max", spec.MaxTemperature) })
        {
            if (limit is TemperatureLimit.AtCase { Value: var value } && (float.IsNaN(value) || Math.Abs((double)value) > NewColonyBounds.MaxTemperature))
            {
                throw BridgeContractException.Contract($"new colony {name} temperature out of range");
            }
        }

        if (spec.MinTemperature is TemperatureLimit.AtCase { Value: var min } && spec.MaxTemperature is TemperatureLimit.AtCase { Value: var max } && min > max)
        {
            throw BridgeContractException.Contract("new colony min temperature exceeds max");
        }

        if (spec.SaveName is null || !NewColonySaveName.IsMatch((spec.SaveName ?? "")))
        {
            throw BridgeContractException.Contract("new colony save name invalid");
        }

        ValidateNewColonyParts(spec);
        if (spec.Ideoligion is not null)
        {
            Orders.ValidateIdeoligionDesign(spec.Ideoligion);
        }
    }

    private static void ValidateNewColonyParts(NewColonySpec spec)
    {
        foreach (var n in new[]
        {
            spec.StartingThings.Count, spec.ForcedTraits.Count, spec.ForcedHediffs.Count, spec.Naked.Count, spec.SetNeedLevels.Count,
            spec.PermaGameConditions.Count, spec.DisallowedBuildings.Count, spec.DisabledIncidents.Count,
        })
        {
            if (n > NewColonyBounds.MaxPartsPerKind)
            {
                throw BridgeContractException.Contract($"new colony scenario parts exceed {NewColonyBounds.MaxPartsPerKind} per kind");
            }
        }

        foreach (var t in spec.StartingThings)
        {
            if (t is null || !Enum.IsDefined(t.Placement)
                || t.ThingDef is null || !WireValidation.IsValidId((t.ThingDef ?? "")) || !ColonyValidation.OptionalId(t.Stuff is not null, (t.Stuff ?? "")) || !ColonyValidation.OptionalId(t.Quality is not null, (t.Quality ?? "")))
            {
                throw BridgeContractException.Contract("new colony starting thing invalid");
            }

            if (t.Count < 1 || t.Count > NewColonyBounds.MaxThingCount)
            {
                throw BridgeContractException.Contract("new colony starting thing count out of range");
            }
        }

        foreach (var t in spec.ForcedTraits)
        {
            if (t is null || t.Trait is null || !WireValidation.IsValidId((t.Trait ?? "")) || t.Degree < -NewColonyBounds.MaxTraitDegree || t.Degree > NewColonyBounds.MaxTraitDegree)
            {
                throw BridgeContractException.Contract("new colony forced trait invalid or degree out of range");
            }

            PartChanceContext(t.Chance, t.Context, "forced trait");
        }

        foreach (var h in spec.ForcedHediffs)
        {
            if (h is null || h.Hediff is null || !WireValidation.IsValidId((h.Hediff ?? "")) || !InRange(h.Severity, 0, NewColonyBounds.MaxSeverity))
            {
                throw BridgeContractException.Contract("new colony forced hediff invalid or severity out of range");
            }

            PartChanceContext(h.Chance, h.Context, "forced hediff");
        }

        foreach (var n in spec.Naked)
        {
            if (n is null)
            {
                throw BridgeContractException.Contract("new colony naked part invalid");
            }

            PartChanceContext(n.Chance, n.Context, "naked");
        }

        foreach (var n in spec.SetNeedLevels)
        {
            if (n is null || n.Need is null || !WireValidation.IsValidId((n.Need ?? "")) || !InRange(n.Level, 0, 1))
            {
                throw BridgeContractException.Contract("new colony need level invalid or out of range");
            }

            PartChanceContext(n.Chance, n.Context, "need level");
        }

        foreach (var (name, ids) in new[]
        {
            ("perma game conditions", spec.PermaGameConditions),
            ("disallowed buildings", spec.DisallowedBuildings),
            ("disabled incidents", spec.DisabledIncidents),
        })
        {
            if (!ValidIds(ids))
            {
                throw BridgeContractException.Contract($"new colony {name} invalid");
            }
        }

        if (!Enum.IsDefined(spec.PlayerFaction))
        {
            throw BridgeContractException.Contract("new colony player faction invalid");
        }

        if (!Enum.IsDefined(spec.ArrivalMethod))
        {
            throw BridgeContractException.Contract("new colony arrival method invalid");
        }
    }

    private static void RequireId(bool present, string value, string name)
    {
        if (!present || !WireValidation.IsValidId(value))
        {
            throw BridgeContractException.Contract($"new colony spec requires {name}");
        }
    }

    // Every id valid and none repeated.
    private static bool ValidIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!WireValidation.IsValidId(id) || !seen.Add(id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRange(float value, double lo, double hi) =>
        !float.IsNaN(value) && (double)value >= lo && (double)value <= hi;

    private static void PartChanceContext(float chance, ScenarioPawnContext context, string what)
    {
        if (!InRange(chance, 0, 1))
        {
            throw BridgeContractException.Contract($"new colony {what} chance out of range");
        }

        if (!Enum.IsDefined(context))
        {
            throw BridgeContractException.Contract($"new colony {what} context invalid");
        }
    }
}
