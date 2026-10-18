#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
namespace HomeBridge.BridgeTools;

/// <summary>
/// Hunt encounter inputs: the colonists who could join a hunt and the huntable animals, each stated as the
/// encounter predictor's actor, plus the map window around them. Every hunter, every prey and the whole window
/// are read (#3341). Observation only: no roster, target or estimate.
/// </summary>
internal static class NativeHuntActivity
{
    const int Margin = 12;

    internal static Obs.HuntActivityRead Read(Map map)
    {
        var hunters = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var center = hunters.Count == 0 ? map.Center : hunters[0].Position;
        // Wild edible animals, nearest to the first hunter; a roster is built from what is here, never from a radius.
        var prey = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == null && p.RaceProps.Animal && !p.Dead
            && p.RaceProps.meatDef?.IsNutritionGivingIngestible == true && !p.Position.Fogged(map))
            .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var hunterReads = hunters.Select(h => Actor(h, true, prey)).ToArray();
        var preyReads = prey.Select(p => Actor(p, false, prey) with { CorpseStore = CorpseStore(map, p) }).ToArray();
        // Until the content identity is hashed the scene is unobserved; a roster is then refused, never predicted against a guess.
        var identity = NativeEncounterIdentity.Current;
        var scene = hunterReads.Length == 0 || preyReads.Length == 0 || identity is null ? null : Scene(map, identity, hunters.Concat(prey).ToArray());
        return new() { Known = true, Hunters = EquatableArray.From(hunterReads), Prey = EquatableArray.From(preyReads), Scene = scene,
            Findings = identity is null ? ["engine_identity_pending", "needs_genes_and_effects_are_not_observed_here"] : ["needs_genes_and_effects_are_not_observed_here"] };
    }

    static Obs.HuntActorRead Actor(Pawn p, bool hunter, List<Pawn> prey)
    {
        var weapon = p.equipment?.Primary;
        var verb = weapon?.GetComp<CompEquippable>()?.AllVerbs.Where(v => !v.verbProps.IsMeleeAttack).OrderByDescending(v => v.verbProps.range).FirstOrDefault();
        var herdSize = !hunter ? prey.Count(q => q.def == p.def && q.Position.DistanceToSquared(p.Position) <= 24 * 24) : 0;
        var meat = p.RaceProps.meatDef;
        var read = new Obs.HuntActorRead(p.GetUniqueLoadID(), p.kindDef.defName, new(p.Position.x, p.Position.z), p.ageTracker.AgeBiologicalYearsFloat)
        {
            Xenotype = p.genes?.Xenotype?.defName ?? "",
            Weapon = Gear(weapon),
            WeaponRange = verb == null ? default : Fact.Known((double)verb.verbProps.range),
            Apparel = EquatableArray.From((p.apparel?.WornApparel ?? []).Select(a => Gear(a)!).Where(g => g != null)),
            Skills = EquatableArray.From((p.skills?.skills ?? []).Select(s => new Obs.HuntSkillRead(s.def.defName, s.Level))),
            Traits = EquatableArray.From((p.story?.traits.allTraits ?? []).Select(t => new Obs.HuntTraitRead(t.def.defName, t.Degree))),
            Health = EquatableArray.From(p.health.hediffSet.hediffs.Where(h => h is not Hediff_MissingPart)
                .Select(h => new Obs.HuntHediffRead(h.def.defName, h.Part?.Label ?? "", h.Severity))),
            MissingParts = EquatableArray.From(p.health.hediffSet.hediffs.OfType<Hediff_MissingPart>().Select(h => h.Part?.Label ?? "")),
            Available = hunter && !p.Downed && !p.InMentalState && !p.Drafted,
            Hauls = hunter && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true,
            Downed = p.Downed, Mental = p.InMentalState,
        };
        if (hunter) return read;
        return read with
        {
            Herd = herdSize > 1 ? "wild-herd" : "",
            Meat = Fact.Known(Math.Max(0d, p.GetStatValue(StatDefOf.MeatAmount))),
            MeatNutrition = meat == null ? default : Fact.Known((double)meat.GetStatValueAbstract(StatDefOf.Nutrition)),
        };
    }

    /// <summary>
    /// Where native would haul the prey's corpse: the store of highest priority whose settings accept its corpse def,
    /// at its cell nearest the prey (StoreUtility's own order: priority, then distance from the corpse). None: null.
    /// </summary>
    static RimGovernor.Brain.Domain.Cell? CorpseStore(Map map, Pawn prey)
    {
        var corpse = prey.RaceProps.corpseDef;
        if (corpse == null) return null;
        var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder
            .Where(g => (g.parent is not Thing parent || parent.Faction == Faction.OfPlayer) && g.parent.HaulDestinationEnabled && g.Settings.AllowedToAccept(corpse) && g.CellsList.Count > 0)
            .ToList();
        if (groups.Count == 0) return null;
        var best = groups[0].Settings.Priority;
        var cell = groups.Where(g => g.Settings.Priority == best).SelectMany(g => g.CellsList)
            .OrderBy(c => c.DistanceToSquared(prey.Position)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        return new RimGovernor.Brain.Domain.Cell(cell.x, cell.z);
    }

    static Obs.HuntGearRead? Gear(ThingWithComps? thing)
    {
        if (thing == null) return null;
        thing.TryGetQuality(out var quality);
        return new(thing.def.defName, thing.Stuff?.defName ?? "", quality.ToString(), thing.HitPoints);
    }

    static Obs.HuntSceneRead Scene(Map map, NativeEncounterIdentity.Identity identity, Pawn[] actors)
    {
        var minX = Math.Max(0, actors.Min(a => a.Position.x) - Margin); var maxX = Math.Min(map.Size.x - 1, actors.Max(a => a.Position.x) + Margin);
        var minZ = Math.Max(0, actors.Min(a => a.Position.z) - Margin); var maxZ = Math.Min(map.Size.z - 1, actors.Max(a => a.Position.z) + Margin);
        // Row runs of terrain/roof; the most common terrain is the scene's base and only departures from it are listed.
        var cells = (from x in Enumerable.Range(minX, maxX - minX + 1) from z in Enumerable.Range(minZ, maxZ - minZ + 1)
                     select (x, z, terrain: map.terrainGrid.TerrainAt(new IntVec3(x, 0, z)).defName, roof: map.roofGrid.RoofAt(x, z)?.defName ?? "")).ToArray();
        var terrain = cells.GroupBy(c => c.terrain).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        var ground = new List<Obs.HuntGroundRead>();
        foreach (var row in cells.Where(c => c.terrain != terrain || c.roof != "").GroupBy(c => c.z).OrderBy(g => g.Key))
        {
            var ordered = row.OrderBy(c => c.x).ToArray();
            for (var i = 0; i < ordered.Length;)
            {
                var j = i;
                while (j + 1 < ordered.Length && ordered[j + 1].x == ordered[j].x + 1 && ordered[j + 1].terrain == ordered[i].terrain && ordered[j + 1].roof == ordered[i].roof) j++;
                ground.Add(new(ordered[i].x, row.Key, ordered[j].x, row.Key, ordered[i].terrain, ordered[i].roof));
                i = j + 1;
            }
        }
        var obstacles = new List<Obs.HuntObstacleRead>();
        foreach (var thing in map.listerThings.AllThings.Where(t => t.Spawned && t.def.passability == Traversability.Impassable && t.Position.x >= minX && t.Position.x <= maxX
                     && t.Position.z >= minZ && t.Position.z <= maxZ).OrderBy(t => t.Position.x).ThenBy(t => t.Position.z).ThenBy(t => t.def.defName, StringComparer.Ordinal))
        {
            obstacles.Add(new(thing.def.defName, thing.Stuff?.defName ?? "", thing.Position.x, thing.Position.z));
        }
        return new(Find.World.info.name + "/" + Find.World.info.seedString, identity.GameBuild, identity.Mods, map.weatherManager.curWeather.defName,
            terrain, minX, minZ, maxX - minX + 1, maxZ - minZ + 1, map.mapTemperature.OutdoorTemp, map.skyManager.CurSkyGlow)
        { Ground = EquatableArray.From(ground), Obstacles = EquatableArray.From(obstacles) };
    }

}
