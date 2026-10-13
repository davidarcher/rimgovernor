using System.Text.RegularExpressions;
using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Lifecycle;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>Bounds a <see cref="NewColonyRequest"/> must respect; the HTTP route and native share them (Go <c>NewColony*</c> consts).</summary>
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

    /// <summary>The fixed <c>player_direction</c> the controller sends on lifecycle save and load (Go <c>LifecycleDirection</c>); the field must be nonzero and carries no other meaning.</summary>
    public const ulong LifecycleDirection = 1;
}

public static partial class BridgeFunctions
{
    private static readonly Regex NewColonySaveName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Go's <c>ValidateNewColonyRequest</c>: checks a request against every bound the wire contract names, before it reaches native.</summary>
    public static void ValidateNewColonyRequest(NewColonyRequest? request)
    {
        if (request is null)
        {
            throw Contract("new colony request required");
        }

        if (!request.HasRequestId || !IsValidId(request.RequestId))
        {
            throw Contract("new colony request requires a request id");
        }

        if (!request.HasTimeoutMs || request.TimeoutMs < NewColonyBounds.MinTimeoutMs || request.TimeoutMs > NewColonyBounds.MaxTimeoutMs)
        {
            throw Contract("new colony timeout out of range");
        }

        ValidateNewColonySpec(request.Spec);
    }

    /// <summary>Go's <c>ValidateNewColonySpec</c>: the spec's presence and range rules. Whether a defName exists is native's call.</summary>
    public static void ValidateNewColonySpec(NewColonySpec? spec)
    {
        if (spec is null)
        {
            throw Contract("new colony spec required");
        }

        RequireId(spec.HasScenario, spec.Scenario, "scenario");
        RequireId(spec.HasSeed, spec.Seed, "seed");
        RequireId(spec.HasDifficulty, spec.Difficulty, "difficulty");
        RequireId(spec.HasStoryteller, spec.Storyteller, "storyteller");
        if (spec.HasWorldTemperature && !IsValidId(spec.WorldTemperature))
        {
            throw Contract("new colony world temperature invalid");
        }

        if (!spec.HasColonistCount || spec.ColonistCount < 1 || spec.ColonistCount > NewColonyBounds.MaxColonists)
        {
            throw Contract("new colony colonist count out of range");
        }

        if (spec.Biomes.Count > NewColonyBounds.MaxBiomes || !ValidIds(spec.Biomes))
        {
            throw Contract("new colony biomes invalid");
        }

        if (!spec.HasMapSize || spec.MapSize < NewColonyBounds.MinMapSize || spec.MapSize > NewColonyBounds.MaxMapSize)
        {
            throw Contract("new colony map size out of range");
        }

        if (!spec.HasPlanetCoverage || double.IsNaN(spec.PlanetCoverage) || spec.PlanetCoverage < NewColonyBounds.MinCoverage || spec.PlanetCoverage > NewColonyBounds.MaxCoverage)
        {
            throw Contract("new colony planet coverage out of range");
        }

        foreach (var (name, present, value) in new[] { ("min", spec.HasMinTemperature, spec.MinTemperature), ("max", spec.HasMaxTemperature, spec.MaxTemperature) })
        {
            if (present && (float.IsNaN(value) || Math.Abs((double)value) > NewColonyBounds.MaxTemperature))
            {
                throw Contract($"new colony {name} temperature out of range");
            }
        }

        if (spec.HasMinTemperature && spec.HasMaxTemperature && spec.MinTemperature > spec.MaxTemperature)
        {
            throw Contract("new colony min temperature exceeds max");
        }

        if (!spec.HasSaveName || !NewColonySaveName.IsMatch(spec.SaveName))
        {
            throw Contract("new colony save name invalid");
        }

        ValidateNewColonyParts(spec);
        if (spec.Ideoligion is not null)
        {
            ValidateIdeoligionDesign(spec.Ideoligion);
        }
    }

    /// <summary>Go's <c>ValidateIdeoligionDesign</c>: shape only. Native owns compatibility, faction restrictions, required issues and the game's selection limits.</summary>
    public static void ValidateIdeoligionDesign(RimGovernor.Protocol.Common.IdeoligionDesign? design)
    {
        if (design is null || !design.HasFluid || design.Memes.Count == 0 || design.Memes.Count > 5 || design.Precepts.Count > 256)
        {
            throw Contract("invalid ideoligion design");
        }

        foreach (var names in new[] { design.Memes, design.Precepts })
        {
            if (!ValidIds(names))
            {
                throw Contract("invalid or duplicate ideoligion selection");
            }
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
                throw Contract($"new colony scenario parts exceed {NewColonyBounds.MaxPartsPerKind} per kind");
            }
        }

        foreach (var t in spec.StartingThings)
        {
            if (t is null || !t.HasPlacement || t.Placement == StartingThingPlacement.Unspecified || !Enum.IsDefined(t.Placement)
                || !t.HasThingDef || !IsValidId(t.ThingDef) || !OptionalId(t.HasStuff, t.Stuff) || !OptionalId(t.HasQuality, t.Quality))
            {
                throw Contract("new colony starting thing invalid");
            }

            if (!t.HasCount || t.Count < 1 || t.Count > NewColonyBounds.MaxThingCount)
            {
                throw Contract("new colony starting thing count out of range");
            }

            if (t.HasAllowRoofed && t.Placement != StartingThingPlacement.Scattered)
            {
                throw Contract("new colony starting thing allow roofed applies to scattered things only");
            }
        }

        foreach (var t in spec.ForcedTraits)
        {
            if (t is null || !t.HasTrait || !IsValidId(t.Trait) || !t.HasDegree || t.Degree < -NewColonyBounds.MaxTraitDegree || t.Degree > NewColonyBounds.MaxTraitDegree)
            {
                throw Contract("new colony forced trait invalid or degree out of range");
            }

            PartChanceContext(t.HasChance, t.Chance, t.HasContext, t.Context, "forced trait");
        }

        foreach (var h in spec.ForcedHediffs)
        {
            if (h is null || !h.HasHediff || !IsValidId(h.Hediff) || !InRange(h.HasSeverity, h.Severity, 0, NewColonyBounds.MaxSeverity))
            {
                throw Contract("new colony forced hediff invalid or severity out of range");
            }

            PartChanceContext(h.HasChance, h.Chance, h.HasContext, h.Context, "forced hediff");
        }

        foreach (var n in spec.Naked)
        {
            if (n is null)
            {
                throw Contract("new colony naked part invalid");
            }

            PartChanceContext(n.HasChance, n.Chance, n.HasContext, n.Context, "naked");
        }

        foreach (var n in spec.SetNeedLevels)
        {
            if (n is null || !n.HasNeed || !IsValidId(n.Need) || !InRange(n.HasLevel, n.Level, 0, 1))
            {
                throw Contract("new colony need level invalid or out of range");
            }

            PartChanceContext(n.HasChance, n.Chance, n.HasContext, n.Context, "need level");
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
                throw Contract($"new colony {name} invalid");
            }
        }

        if (spec.HasPlayerFaction && (spec.PlayerFaction == ScenarioPlayerFaction.Unspecified || !Enum.IsDefined(spec.PlayerFaction)))
        {
            throw Contract("new colony player faction invalid");
        }

        if (spec.HasArrivalMethod && (spec.ArrivalMethod == ScenarioArrivalMethod.Unspecified || !Enum.IsDefined(spec.ArrivalMethod)))
        {
            throw Contract("new colony arrival method invalid");
        }
    }

    private static void RequireId(bool present, string value, string name)
    {
        if (!present || !IsValidId(value))
        {
            throw Contract($"new colony spec requires {name}");
        }
    }

    // Go's validIDs(ids, true): every id valid and none repeated.
    private static bool ValidIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRange(bool present, float value, double lo, double hi) =>
        present && !float.IsNaN(value) && (double)value >= lo && (double)value <= hi;

    private static void PartChanceContext(bool hasChance, float chance, bool hasContext, ScenarioPawnContext context, string what)
    {
        if (!InRange(hasChance, chance, 0, 1))
        {
            throw Contract($"new colony {what} chance out of range");
        }

        if (!hasContext || context == ScenarioPawnContext.Unspecified)
        {
            throw Contract($"new colony {what} requires a context");
        }

        if (!Enum.IsDefined(context))
        {
            throw Contract($"new colony {what} context invalid");
        }
    }
}
