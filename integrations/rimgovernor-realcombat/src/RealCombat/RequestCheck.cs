using RimWorld;
using Verse;

namespace RimGovernor.RealCombat;

/// <summary>
/// The strict half of the request format: everything the host cannot honor rejects the whole request, naming the field,
/// before the arena is built, so no pawn is generated for a request that fails. Unknown JSON fields are rejected by the
/// deserializer (<see cref="RequestJson"/>); this checks the values against the game's defs and the races' bodies.
/// </summary>
public static class RequestCheck
{
    private const float AgeEpsilon = 1e-4f;

    /// <summary>Throws <see cref="RealFightRejected"/> listing every problem; returns when the request is honorable.</summary>
    public static void Validate(RealFightRequest req)
    {
        var errors = new List<string>();
        if (req.Schema != RealFightRequest.CurrentSchema)
        {
            errors.Add($"Schema: the request states {req.Schema}, this host reads {RealFightRequest.CurrentSchema}");
        }

        if (req.Width <= 0 || req.Height <= 0)
        {
            errors.Add($"Width/Height: {req.Width}x{req.Height} is not a map size");
        }

        if (req.MaxTicks <= 0)
        {
            errors.Add($"MaxTicks: {req.MaxTicks} is not positive");
        }

        if (req.Seeds.Count == 0)
        {
            errors.Add("Seeds: no seed to fight");
        }

        Def<TerrainDef>(errors, "Terrain", req.Terrain, optional: true);
        for (var i = 0; i < req.Ground.Count; i++)
        {
            var g = req.Ground[i];
            var at = $"Ground[{i}]";
            Def<TerrainDef>(errors, at + ".Terrain", g.Terrain, optional: true);
            Def<RoofDef>(errors, at + ".Roof", g.Roof, optional: true);
            if (g.Terrain.Length == 0 && g.Roof.Length == 0)
            {
                errors.Add($"{at}: states neither Terrain nor Roof");
            }

            if (g.MinX > g.MaxX || g.MinZ > g.MaxZ)
            {
                errors.Add($"{at}.MinX/MaxX/MinZ/MaxZ: {g.MinX}..{g.MaxX} x {g.MinZ}..{g.MaxZ} is an empty region");
            }

            Cell(errors, at + ".Min", req, g.MinX, g.MinZ);
            Cell(errors, at + ".Max", req, g.MaxX, g.MaxZ);
        }

        Def<WeatherDef>(errors, "Weather", req.Weather, optional: true);
        Def<FactionDef>(errors, "HostileFaction", req.HostileFaction, optional: false);
        for (var i = 0; i < req.Things.Count; i++)
        {
            var t = req.Things[i];
            var at = $"Things[{i}]";
            Def<ThingDef>(errors, at + ".Def", t.Def, optional: false);
            Def<ThingDef>(errors, at + ".Stuff", t.Stuff, optional: true);
            Cell(errors, at, req, t.X, t.Z);
        }

        var ids = new HashSet<string>();
        for (var i = 0; i < req.Pawns.Count; i++)
        {
            Pawn(errors, req, req.Pawns[i], $"Pawns[{i}]", ids);
        }

        var departing = new HashSet<string>();
        for (var i = 0; i < req.Departures.Count; i++)
        {
            var d = req.Departures[i];
            var at = $"Departures[{i}]";
            if (!ids.Contains(d.Id))
            {
                errors.Add($"{at}.Id: '{d.Id}' names no pawn of the request");
            }
            else if (!departing.Add(d.Id))
            {
                errors.Add($"{at}.Id: '{d.Id}' departs twice");
            }

            if (d.Tick < 0 || d.Tick > req.MaxTicks)
            {
                errors.Add($"{at}.Tick: {d.Tick} is outside 0-{req.MaxTicks} (MaxTicks)");
            }

            if (d.Kind is not (RealDeparture.Leave or RealDeparture.Flee))
            {
                errors.Add($"{at}.Kind: '{d.Kind}' is neither {RealDeparture.Leave} nor {RealDeparture.Flee}");
            }
        }

        if (errors.Count > 0)
        {
            throw new RealFightRejected(errors);
        }
    }

    private static void Pawn(List<string> errors, RealFightRequest req, RealPawn p, string at, HashSet<string> ids)
    {
        if (p.Id.Length == 0 || !ids.Add(p.Id))
        {
            errors.Add($"{at}.Id: '{p.Id}' is empty or repeats another pawn's");
        }

        if (p.Side is not ("colonist" or "hostile"))
        {
            errors.Add($"{at}.Side: '{p.Side}' is neither colonist nor hostile");
        }

        Cell(errors, at, req, p.X, p.Z);
        var kind = Def<PawnKindDef>(errors, at + ".Kind", p.Kind, optional: false);
        var weapon = Def<ThingDef>(errors, at + ".Weapon", p.Weapon, optional: true, check: d => d.IsWeapon ? null : "is not a weapon");
        Def<ThingDef>(errors, at + ".WeaponStuff", p.WeaponStuff, optional: true);
        if (p.Weapon.Length == 0 && (p.WeaponQuality.Length > 0 || p.WeaponHitPoints != 0))
        {
            errors.Add($"{at}.WeaponQuality/WeaponHitPoints: the pawn has no weapon");
        }

        Condition(errors, at + ".WeaponQuality", at + ".WeaponHitPoints", weapon, p.WeaponQuality, p.WeaponHitPoints);
        for (var i = 0; i < p.Worn.Count; i++)
        {
            var wear = Def<ThingDef>(errors, $"{at}.Worn[{i}].Def", p.Worn[i].Def, optional: false, check: d => d.IsApparel ? null : "is not apparel");
            Def<ThingDef>(errors, $"{at}.Worn[{i}].Stuff", p.Worn[i].Stuff, optional: true);
            Condition(errors, $"{at}.Worn[{i}].Quality", $"{at}.Worn[{i}].HitPoints", wear, p.Worn[i].Quality, p.Worn[i].HitPoints);
        }

        foreach (var (skill, level) in p.Skills)
        {
            Def<SkillDef>(errors, $"{at}.Skills.{skill}", skill, optional: false);
            if (level is < 0 or > 20)
            {
                errors.Add($"{at}.Skills.{skill}: level {level} is outside 0-20");
            }
        }

        for (var i = 0; i < p.Traits.Count; i++)
        {
            var t = p.Traits[i];
            var def = Def<TraitDef>(errors, $"{at}.Traits[{i}].Def", t.Def, optional: false);
            if (def is not null && !def.degreeDatas.Any(d => d.degree == t.Degree))
            {
                errors.Add($"{at}.Traits[{i}].Degree: {t.Def} has no degree {t.Degree}");
            }
        }

        foreach (var (name, i) in p.Carried.Select((n, i) => (n, i)))
        {
            Def<ThingDef>(errors, $"{at}.Carried[{i}]", name, optional: false);
        }

        foreach (var (name, i) in p.Psycasts.Select((n, i) => (n, i)))
        {
            Def<AbilityDef>(errors, $"{at}.Psycasts[{i}]", name, optional: false);
        }

        Def<RoyalTitleDef>(errors, at + ".Title", p.Title, optional: true);
        foreach (var (name, i) in p.Permits.Select((n, i) => (n, i)))
        {
            Def<RoyalTitlePermitDef>(errors, $"{at}.Permits[{i}]", name, optional: false);
        }

        if ((p.Psycasts.Count > 0 || p.Title.Length > 0 || p.Permits.Count > 0 || p.Favor != 0) && !ModsConfig.RoyaltyActive)
        {
            errors.Add($"{at}.Psycasts/Title/Permits/Favor: the game has no Royalty");
        }

        if (kind is null)
        {
            return;
        }

        if (kind.RaceProps.Humanlike)
        {
            if (p.Age <= 0)
            {
                errors.Add($"{at}.Age: a humanlike pawn states its age");
            }

            Def<XenotypeDef>(errors, at + ".Xenotype", p.Xenotype, optional: false);
        }
        else
        {
            if (p.Age != 0 || p.Xenotype.Length > 0)
            {
                errors.Add($"{at}.Age/Xenotype: {p.Kind} is not humanlike; age and xenotype are not imported for it");
            }

            if (p.Weapon.Length > 0 || p.Worn.Count > 0 || p.Skills.Count > 0 || p.Traits.Count > 0 || p.Carried.Count > 0 || p.Psycasts.Count > 0 || p.Title.Length > 0 || p.Permits.Count > 0 || p.Favor != 0)
            {
                errors.Add($"{at}: {p.Kind} is not humanlike; weapon, worn, skills, traits, carried, psycasts and title are not imported for it");
            }
        }

        Health(errors, kind, p, at);
    }

    // Hediffs and missing parts resolve against the race's body, so a part the race lacks (or names twice) rejects here.
    private static void Health(List<string> errors, PawnKindDef kind, RealPawn p, string at)
    {
        var parts = kind.race.race.body.AllParts;
        var missing = new List<BodyPartRecord>();
        for (var i = 0; i < p.Missing.Count; i++)
        {
            var part = Part(errors, $"{at}.Missing[{i}]", parts, p.Missing[i], kind.race.defName);
            if (part is null)
            {
                continue;
            }

            if (missing.Any(m => Covers(m, part) || Covers(part, m)))
            {
                errors.Add($"{at}.Missing[{i}]: '{p.Missing[i]}' is already missing, or contains or sits inside another missing part");
            }

            missing.Add(part);
        }

        for (var i = 0; i < p.Hediffs.Count; i++)
        {
            var h = p.Hediffs[i];
            var def = Def<HediffDef>(errors, $"{at}.Hediffs[{i}].Def", h.Def, optional: false);
            BodyPartRecord? part = null;
            if (h.Part.Length > 0)
            {
                part = Part(errors, $"{at}.Hediffs[{i}].Part", parts, h.Part, kind.race.defName);
                if (part is not null && missing.Any(m => Covers(m, part)))
                {
                    errors.Add($"{at}.Hediffs[{i}].Part: '{h.Part}' is missing (or inside a missing part)");
                }
            }

            if (def is null)
            {
                continue;
            }

            if (def.injuryProps != null && h.Part.Length == 0)
            {
                errors.Add($"{at}.Hediffs[{i}].Part: {h.Def} is an injury and needs a part");
            }

            if (h.Severity <= 0 || h.Severity > def.maxSeverity)
            {
                errors.Add($"{at}.Hediffs[{i}].Severity: {h.Severity} is outside (0, {def.maxSeverity}] for {h.Def}");
            }
        }
    }

    private static bool Covers(BodyPartRecord ancestor, BodyPartRecord part)
    {
        for (var at = part; at is not null; at = at.parent)
        {
            if (at == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static BodyPartRecord? Part(List<string> errors, string field, IEnumerable<BodyPartRecord> parts, string label, string race)
    {
        var found = parts.Where(r => r.Label == label).ToList();
        if (found.Count == 1)
        {
            return found[0];
        }

        errors.Add(found.Count == 0 ? $"{field}: {race} has no body part '{label}'" : $"{field}: '{label}' names {found.Count} parts of {race}");
        return null;
    }

    // Quality is a QualityCategory name on a def that carries quality; hit points are positive (the maximum depends on
    // stuff and quality, so an over-maximum value is rejected by the readback gate).
    private static void Condition(List<string> errors, string qualityField, string hitPointsField, ThingDef? def, string quality, int hitPoints)
    {
        if (quality.Length > 0)
        {
            if (!Enum.TryParse<QualityCategory>(quality, out var q) || !Enum.IsDefined(q) || q.ToString() != quality)
            {
                errors.Add($"{qualityField}: '{quality}' is not a quality ({string.Join(", ", Enum.GetNames<QualityCategory>())})");
            }
            else if (def is not null && !def.comps.Any(c => typeof(CompQuality).IsAssignableFrom(c.compClass)))
            {
                errors.Add($"{qualityField}: {def.defName} has no quality");
            }
        }

        if (hitPoints < 0)
        {
            errors.Add($"{hitPointsField}: {hitPoints} is negative (0 is full condition)");
        }
        else if (hitPoints > 0 && def is not null && def.useHitPoints == false)
        {
            errors.Add($"{hitPointsField}: {def.defName} has no hit points");
        }
    }

    /// <summary>The terrain and roof the request states for a cell: the base terrain, then each region covering it in order. Empty roof is none.</summary>
    internal static (string Terrain, string Roof) GroundAt(RealFightRequest req, int x, int z)
    {
        var terrain = req.Terrain;
        var roof = "";
        foreach (var g in req.Ground)
        {
            if (x < g.MinX || x > g.MaxX || z < g.MinZ || z > g.MaxZ)
            {
                continue;
            }

            terrain = g.Terrain.Length > 0 ? g.Terrain : terrain;
            roof = g.Roof.Length > 0 ? g.Roof : roof;
        }

        return (terrain, roof);
    }

    private static void Cell(List<string> errors, string at, RealFightRequest req, int x, int z)
    {
        if (x < 0 || z < 0 || x >= req.Width || z >= req.Height)
        {
            errors.Add($"{at}.X/Z: {x},{z} is outside the {req.Width}x{req.Height} map");
        }
    }

    private static T? Def<T>(List<string> errors, string field, string name, bool optional, Func<T, string?>? check = null)
        where T : Def
    {
        if (name.Length == 0)
        {
            if (!optional)
            {
                errors.Add($"{field}: a {typeof(T).Name} is required");
            }

            return null;
        }

        var def = DefDatabase<T>.GetNamedSilentFail(name);
        if (def is null)
        {
            errors.Add($"{field}: the game has no {typeof(T).Name} '{name}'");
            return null;
        }

        var problem = check?.Invoke(def);
        if (problem is not null)
        {
            errors.Add($"{field}: '{name}' {problem}");
            return null;
        }

        return def;
    }

    /// <summary>True when the readback age equals the stated one.</summary>
    internal static bool SameAge(double stated, double read) => Math.Abs(stated - read) <= AgeEpsilon;
}
