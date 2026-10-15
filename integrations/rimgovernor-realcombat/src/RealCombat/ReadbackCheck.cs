using RimWorld;
using Verse;

namespace RimGovernor.RealCombat;

/// <summary>
/// Reads every imported field back from the game objects and compares it with the request. A mismatch fails the run by
/// field name: the game did not honor something the request stated, so the fight would not be the one asked for.
/// </summary>
internal static class ReadbackCheck
{
    private const double SeverityEpsilon = 1e-4;

    /// <summary>The arena's side of the readback (size, terrain, weather, faction, standing things), compared with the request.</summary>
    internal static RealReadback Arena(RealFightRequest req, Map map, Faction hostile)
    {
        var errors = new List<string>();
        var terrains = map.AllCells.Select(c => map.terrainGrid.TerrainAt(c).defName).Distinct().Take(2).ToList();
        var things = new List<RealThing>();
        for (var i = 0; i < req.Things.Count; i++)
        {
            var t = req.Things[i];
            var held = map.thingGrid.ThingsListAtFast(new IntVec3(t.X, 0, t.Z)).FirstOrDefault(x => x.def.defName == t.Def);
            if (held is null)
            {
                errors.Add($"Things[{i}]: no {t.Def} at {t.X},{t.Z}");
                continue;
            }

            var read = new RealThing(held.def.defName, held.Stuff?.defName ?? "", held.Position.x, held.Position.z);
            things.Add(read);
            Same(errors, $"Things[{i}].Stuff", t.Stuff, read.Stuff);
        }

        var readback = new RealReadback
        {
            Width = map.Size.x,
            Height = map.Size.z,
            Terrain = terrains.Count == 1 ? terrains[0] : "mixed",
            Weather = map.weatherManager.curWeather.defName,
            HostileFaction = hostile.def.defName,
            Things = things,
        };
        Same(errors, "Width", req.Width, readback.Width);
        Same(errors, "Height", req.Height, readback.Height);
        if (req.Terrain.Length > 0)
        {
            Same(errors, "Terrain", req.Terrain, readback.Terrain);
        }

        if (req.Weather.Length > 0)
        {
            Same(errors, "Weather", req.Weather, readback.Weather);
        }

        Same(errors, "HostileFaction", req.HostileFaction, readback.HostileFaction);
        Throw(errors);
        return readback;
    }

    /// <summary>One spawned pawn as the game holds it, compared with the spec it was built from.</summary>
    internal static RealPawnReadback Pawn(RealPawn spec, Pawn pawn, int index)
    {
        var at = $"Pawns[{index}]";
        var errors = new List<string>();
        var humanlike = pawn.RaceProps.Humanlike;
        var empire = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Empire);
        var hediffs = pawn.health.hediffSet.hediffs;
        var read = new RealPawnReadback
        {
            Id = spec.Id,
            Side = pawn.Faction == Faction.OfPlayer ? "colonist" : "hostile",
            Kind = pawn.kindDef.defName,
            X = pawn.Position.x,
            Z = pawn.Position.z,
            Age = pawn.ageTracker.AgeBiologicalYearsFloat,
            Xenotype = pawn.genes?.Xenotype?.defName ?? "",
            Weapon = pawn.equipment?.Primary?.def.defName ?? "",
            WeaponStuff = pawn.equipment?.Primary?.Stuff?.defName ?? "",
            Worn = [.. (pawn.apparel?.WornApparel ?? []).Select(a => new RealWear(a.def.defName, a.Stuff?.defName ?? ""))],
            Skills = spec.Skills.Keys.ToDictionary(k => k, k => pawn.skills.GetSkill(DefDatabase<SkillDef>.GetNamed(k)).Level),
            Traits = [.. (pawn.story?.traits.allTraits ?? []).Select(t => new RealTrait(t.def.defName, t.Degree))],
            Carried = [.. pawn.inventory?.innerContainer.Select(t => t.def.defName) ?? []],
            Hediffs =
            [
                .. hediffs
                    .Where(h => h is not Hediff_MissingPart && !(spec.Psycasts.Count > 0 && h.def == HediffDefOf.PsychicAmplifier))
                    .Select(h => new RealHediff(h.def.defName, h.Part?.Label ?? "", h.Severity)),
            ],
            // The game also records each child of a missing part as missing; the request names the part that was lost.
            Missing = [.. hediffs.OfType<Hediff_MissingPart>().Where(h => h.Part.parent is null || !pawn.health.hediffSet.PartIsMissing(h.Part.parent)).Select(h => h.Part.Label)],
            Psycasts = [.. pawn.abilities?.abilities.Select(a => a.def.defName) ?? []],
            Title = empire is null ? "" : pawn.royalty?.GetCurrentTitle(empire)?.defName ?? "",
            Favor = empire is null ? 0 : pawn.royalty?.GetFavor(empire) ?? 0,
            Permits = [.. empire is null ? [] : pawn.royalty?.PermitsFromFaction(empire).Select(p => p.Permit.defName) ?? []],
            Dead = pawn.Dead,
        };

        Same(errors, at + ".Side", spec.Side, read.Side);
        Same(errors, at + ".Kind", spec.Kind, read.Kind);
        Same(errors, at + ".X", spec.X, read.X);
        Same(errors, at + ".Z", spec.Z, read.Z);
        if (humanlike)
        {
            if (!RequestCheck.SameAge(spec.Age, read.Age) || !RequestCheck.SameAge(spec.Age, pawn.ageTracker.AgeChronologicalYearsFloat))
            {
                errors.Add($"{at}.Age: requested {spec.Age}, the game holds {read.Age} biological and {pawn.ageTracker.AgeChronologicalYearsFloat} chronological");
            }

            Same(errors, at + ".Xenotype", spec.Xenotype, read.Xenotype);
            Same(errors, at + ".Weapon", spec.Weapon, read.Weapon);
            Same(errors, at + ".WeaponStuff", spec.WeaponStuff, read.WeaponStuff);
            Same(errors, at + ".Worn", Sorted(spec.Worn.Select(w => $"{w.Def}/{w.Stuff}")), Sorted(read.Worn.Select(w => $"{w.Def}/{w.Stuff}")));
            foreach (var (skill, level) in spec.Skills)
            {
                Same(errors, $"{at}.Skills.{skill}", level, read.Skills[skill]);
            }

            Same(errors, at + ".Traits", Sorted(spec.Traits.Select(t => $"{t.Def}/{t.Degree}")), Sorted(read.Traits.Select(t => $"{t.Def}/{t.Degree}")));
            // The generator's own inventory (a raider's medicine) is not stripped yet, so the request's items must be present, not alone.
            var spare = read.Carried.ToList();
            foreach (var item in spec.Carried.Where(c => !spare.Remove(c)))
            {
                errors.Add($"{at}.Carried: requested '{item}', the game holds [{string.Join(", ", read.Carried)}]");
            }

            Same(errors, at + ".Psycasts", Sorted(spec.Psycasts), Sorted(read.Psycasts));
            Same(errors, at + ".Title", spec.Title, read.Title);
            Same(errors, at + ".Favor", spec.Favor, read.Favor);
            Same(errors, at + ".Permits", Sorted(spec.Permits), Sorted(read.Permits));
        }

        Same(errors, at + ".Missing", Sorted(spec.Missing), Sorted(read.Missing));
        Hediffs(errors, at + ".Hediffs", spec.Hediffs, read.Hediffs);
        if (read.Dead)
        {
            errors.Add($"{at}: dead on import");
        }

        Throw(errors);
        return read;
    }

    private static void Hediffs(List<string> errors, string field, IReadOnlyList<RealHediff> asked, IReadOnlyList<RealHediff> held)
    {
        static List<RealHediff> Order(IEnumerable<RealHediff> hs) => [.. hs.OrderBy(h => h.Def, StringComparer.Ordinal).ThenBy(h => h.Part, StringComparer.Ordinal).ThenBy(h => h.Severity)];
        var a = Order(asked);
        var h = Order(held);
        var same = a.Count == h.Count && a.Zip(h).All(p => p.First.Def == p.Second.Def && p.First.Part == p.Second.Part && Math.Abs(p.First.Severity - p.Second.Severity) <= SeverityEpsilon);
        if (!same)
        {
            errors.Add($"{field}: requested [{string.Join(", ", a)}], the game holds [{string.Join(", ", h)}]");
        }
    }

    private static string Sorted(IEnumerable<string> items) => string.Join(",", items.OrderBy(i => i, StringComparer.Ordinal));

    private static void Same<T>(List<string> errors, string field, T asked, T held)
    {
        if (!EqualityComparer<T>.Default.Equals(asked, held))
        {
            errors.Add($"{field}: requested '{asked}', the game holds '{held}'");
        }
    }

    private static void Throw(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new RealFightRejected([.. errors.Select(e => "readback " + e)]);
        }
    }
}
