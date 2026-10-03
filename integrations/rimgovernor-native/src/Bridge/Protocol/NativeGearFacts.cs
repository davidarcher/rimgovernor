#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Defs = RimGovernor.Protocol.Defs;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    internal static class NativeGearFacts
    {
        internal static Obs.GearSnapshot Read(Map map, Common.ObservationContext context)
        {
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            var result = new Obs.GearSnapshot { Context = context.Clone()};
            ReadClimate(map, result);
            var catalog = Catalog();
            var apparelOnMap = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().ToList();
            var byId = new Dictionary<string, Apparel>();
            foreach (var apparel in apparelOnMap) byId[apparel.GetUniqueLoadID()] = apparel;
            var billOptions = new Dictionary<RecipeDef, Obs.GearLoadoutOption?>();
            var stored = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>()
                .Where(a => a.IsInValidStorage() && !a.IsForbidden(Faction.OfPlayer) && !a.WornByCorpse
                    && (!a.def.useHitPoints || (float)a.HitPoints / a.MaxHitPoints > .5f))
                .GroupBy(a => new { Def = a.def.defName, Stuff = a.Stuff?.defName ?? "",
                    Quality = a.TryGetQuality(out var q) ? (int)q : 2,
                    Band = a.def.useHitPoints ? Math.Min(9, (int)(10f * a.HitPoints / a.MaxHitPoints)) : 9 })
                .OrderBy(g => g.Key.Def).ThenBy(g => g.Key.Stuff).ThenBy(g => g.Key.Quality).ThenBy(g => g.Key.Band).ToList();
            result.StoredApparel = new Obs.GearStorage { };
            foreach (var group in stored)
                result.StoredApparel.Rows.Add(new Obs.GearStock { DefName = group.Key.Def, Stuff = group.Key.Stuff,
                    Quality = group.Key.Quality, HpBand = group.Key.Band, Count = group.Sum(a => a.stackCount) });
            // The census carries each candidate's supply token the way the
            // supply census does (issue #233); the WEAR pawn order (#939)
            // does not check it.
            foreach (var pawn in people) {
                var refusal = GearUpkeepTools.Available(pawn);
                var row = new Obs.GearLoadout {
                    Pawn = NativeRef.Thing(pawn),
                    Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = Id(pawn.GetUniqueLoadID()), Token = GearUpkeepTools.Identity(pawn) },
                    ComfortableMinC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                    ApparelPolicy = pawn.outfits?.CurrentApparelPolicy == null ? null : NativeApparelPolicyOperations.Read(pawn),
                    Equipment = Equipment(pawn),
                    // The wear inputs the apparel rows cannot say: Go applies the
                    // wear filter (gender, stage, body part groups) from the rows.
                    Gender = (Defs.Gender)(int)pawn.gender,
                    DevelopmentalStage = (Defs.DevelopmentalStage)(int)pawn.DevelopmentalStage
                };
                row.BodyPartGroups.Add(PresentGroups(pawn));
                if (refusal != null) row.Blocker = Text(refusal);
                if (refusal == null) {
                    // Every eligible loose item is offered, best gain first
                    // (issue #769: a bound offered every pawn the same few shirts).
                    // Candidates are apparel only: they feed the wear order
                    // (NativeGearOperations, JobDefOf.Wear), which looks its
                    // target up among loose apparel. Loose weapons are the
                    // equip family's (PAWN_ORDER_KIND_EQUIP); listing them
                    // here had a WoodLog admitted as apparel wear and refused
                    // on every attempt (issue #339).
                    // Pathing is the costly check, so it runs only for an
                    // item whose gain already qualifies.
                    var candidates = GearUpkeepTools.WithGainScorer(pawn, score => {
                        var found = new List<KeyValuePair<Thing, float>>();
                        foreach (var apparel in apparelOnMap) {
                            if (GearUpkeepTools.Screened(pawn, apparel) != null) continue;
                            var gain = score(apparel);
                            if (gain >= .05f && GearUpkeepTools.Reachable(pawn, apparel)) found.Add(new KeyValuePair<Thing, float>(apparel, gain));
                        }
                        return found;
                    });
                    foreach (var candidate in candidates.OrderByDescending(c => c.Value).ThenBy(c => c.Key.thingIDNumber))
                        row.Candidates.Add(new Obs.GearCandidate { Item = Candidate(candidate.Key, context), Gain = Number(candidate.Value) });
                }
                row.LoadoutModel = Model(map, pawn, row, catalog, byId, billOptions);
                result.Pawns.Add(row);
            }
            return result;
        }

        // The body part groups the pawn still has a part in (a part that is not missing).
        private static List<string> PresentGroups(Pawn pawn)
        {
            var parts = pawn.health.hediffSet.GetNotMissingParts().ToList();
            return DefDatabase<BodyPartGroupDef>.AllDefsListForReading.Where(g => parts.Any(part => part.IsInGroup(g)))
                .Select(g => Id(g.defName)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // The Go loadout model's catalog is at most 64 unworn options; loose
        // or stored candidates take up to half, best native gain first.
        private const int ModelOptions = 64, ModelPhysical = 32;

        // Producible apparel: one recipe per definition whose research is
        // finished, in stable definition order.
        private static List<RecipeDef> Catalog() => DefDatabase<RecipeDef>.AllDefsListForReading
            .Where(r => r.ProducedThingDef != null && r.ProducedThingDef.IsApparel && r.AvailableNow)
            .GroupBy(r => r.ProducedThingDef).Select(g => g.OrderBy(r => r.defName, StringComparer.Ordinal).First())
            .OrderBy(r => r.ProducedThingDef.defName, StringComparer.Ordinal).ToList();

        private static Obs.GearLoadoutModel Model(Map map, Pawn pawn, Obs.GearLoadout row, List<RecipeDef> catalog,
            Dictionary<string, Apparel> things, Dictionary<RecipeDef, Obs.GearLoadoutOption?> billOptions)
        {
            var model = new Obs.GearLoadoutModel();
            if (pawn.story?.traits != null)
                foreach (var trait in pawn.story.traits.allTraits) model.Traits.Add(new Obs.Trait { DefName = Id(trait.def.defName), Degree = trait.Degree });
            if (pawn.apparel != null)
                foreach (var worn in pawn.apparel.WornApparel) {
                    var option = Physical(worn, "worn");
                    option.Locked = pawn.apparel.IsLocked(worn) || pawn.outfits?.forcedHandler.AllowedToAutomaticallyDrop(worn) == false;
                    model.Worn.Add(option);
                }
            foreach (var candidate in row.Candidates.Take(ModelPhysical))
                if (things.TryGetValue(candidate.Item.Thing.Id, out var apparel))
                    model.Options.Add(Physical(apparel, apparel.IsInValidStorage() ? "stored" : "loose"));
            foreach (var recipe in catalog) {
                if (model.Options.Count >= ModelOptions) break;
                var def = recipe.ProducedThingDef;
                if (!def.apparel.CorrectGenderForWearing(pawn.gender) || !def.apparel.developmentalStageFilter.Has(pawn.DevelopmentalStage)
                    || !ApparelUtility.HasPartsToWear(pawn, def)) continue;
                // A bill option does not depend on the pawn: built once per census.
                if (!billOptions.TryGetValue(recipe, out var option)) billOptions[recipe] = option = BillOption(map, recipe);
                if (option != null) model.Options.Add(option.Clone());
            }
            return model;
        }

        // One stuff per definition: the allowed stuff with the most stock.
        private static Obs.GearLoadoutOption? BillOption(Map map, RecipeDef recipe)
        {
            var def = recipe.ProducedThingDef;
            var stuff = def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def).OrderByDescending(s => map.resourceCounter.GetCount(s))
                .ThenBy(s => s.defName, StringComparer.Ordinal).FirstOrDefault() : null;
            if (def.MadeFromStuff && stuff == null) return null;
            var option = Option("bill:" + def.defName + "/" + (stuff?.defName ?? ""), def, stuff, "bill");
            var research = new List<ResearchProjectDef>();
            if (recipe.researchPrerequisite != null) research.Add(recipe.researchPrerequisite);
            if (recipe.researchPrerequisites != null) research.AddRange(recipe.researchPrerequisites);
            option.Research.Add(research.Select(r => Id(r.defName)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
            foreach (var cost in def.CostListAdjusted(stuff, false))
                option.Ingredients.Add(new Obs.Quantity { DefName = Id(cost.thingDef.defName), Units = cost.count });
            return option;
        }

        private static Obs.GearLoadoutOption Physical(Apparel apparel, string source)
        {
            var row = Option(apparel.GetUniqueLoadID(), apparel.def, apparel.Stuff, source);
            if (apparel.TryGetQuality(out var quality)) row.Quality = (int)quality;
            if (apparel.def.useHitPoints) row.Condition = Number(Math.Min(1.0, (double)apparel.HitPoints / apparel.MaxHitPoints));
            row.Tainted = apparel.WornByCorpse;
            return row;
        }

        // The instance facts of one option at Normal quality; layers, groups and the
        // def x stuff stats are the catalog's. The smoke-pop verb is the one def fact
        // still sent: the catalog's def rows do not carry verbs.
        private static Obs.GearLoadoutOption Option(string id, ThingDef def, ThingDef? stuff, string source)
        {
            var row = new Obs.GearLoadoutOption { Id = Id(id), DefName = Id(def.defName), Source = source, Quality = (int)QualityCategory.Normal,
                Condition = 1, Smokepop = def.Verbs?.Any(v => v.verbClass == typeof(Verb_SmokePop)) == true };
            if (stuff != null) row.Stuff = Id(stuff.defName);
            return row;
        }

        private static void ReadClimate(Map map, Obs.GearSnapshot result)
        {
            var longitude = Find.WorldGrid.LongLatOf(map.Tile).x;
            var offset = GenDate.LocalTicksOffsetFromLongitude(longitude);
            var localTicks = (long)GenTicks.TicksAbs + offset;
            result.CurrentTwelfth = (uint)GenDate.Twelfth(GenTicks.TicksAbs, longitude);
            result.TicksToNextTwelfth = GenDate.TicksPerTwelfth
                - (int)((localTicks % GenDate.TicksPerTwelfth + GenDate.TicksPerTwelfth) % GenDate.TicksPerTwelfth);
            // Sample the middle of each local-calendar twelfth. Seasonal
            // temperature excludes weather and indoor heating; Go applies the
            // bounded weather row only while the condition remains active.
            for (var twelfth = 0; twelfth < GenDate.TwelfthsPerYear; twelfth++) {
                var sampleTick = (int)(twelfth * GenDate.TicksPerTwelfth + GenDate.TicksPerTwelfth / 2 - offset);
                result.OutdoorTemperatureByTwelfthC.Add(GenTemperature.GetTemperatureFromSeasonAtTile(sampleTick, map.Tile));
            }
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            // Normally these events exclude each other. If mods overlap them,
            // retain the strongest current offset, then stable native identity.
            var weather = conditions
                .Where(c => (c.def == GameConditionDefOf.ColdSnap || c.def == GameConditionDefOf.HeatWave)
                    && (c.Permanent || c.TicksLeft > 0))
                .OrderByDescending(c => Math.Abs(c.TemperatureOffset())).ThenBy(c => c.uniqueID).FirstOrDefault();
            if (weather != null) result.ActiveWeather = new Obs.GearWeatherCondition {
                DefName = weather.def.defName,
                RemainingTicks = weather.Permanent ? -1 : Math.Max(0, weather.TicksLeft),
                TemperatureOffsetC = weather.TemperatureOffset()
            };
        }

        private static Obs.PawnEquipment Equipment(Pawn pawn)
        {
            var result = new Obs.PawnEquipment();
            if (pawn.equipment == null) result.Issues.Add(Issue("equipped", Common.UnavailableReason.ReadFailed, "No equipment tracker."));
            else {
                foreach (var thing in pawn.equipment.AllEquipmentListForReading) result.Equipped.Add(Gear(thing));
                result.Armed = pawn.equipment.Primary != null;
                if (pawn.equipment.Primary != null) result.PrimaryId = Id(pawn.equipment.Primary.GetUniqueLoadID());
                else result.Issues.Add(Issue("primary_id", Common.UnavailableReason.NotApplicable, "No equipped primary weapon."));
            }
            if (pawn.apparel == null) result.Issues.Add(Issue("apparel", Common.UnavailableReason.ReadFailed, "No apparel tracker."));
            else {
                foreach (var apparel in pawn.apparel.WornApparel) {
                    var item = Gear(apparel);
                    item.Forced = !pawn.outfits.forcedHandler.AllowedToAutomaticallyDrop(apparel);
                    item.Locked = pawn.apparel.IsLocked(apparel);
                    result.Apparel.Add(item);
                }
            }
            result.Issues.Add(Issue("inventory_weapons", Common.UnavailableReason.NotRequested, "Loadout upkeep excludes inventory."));
            result.Issues.Add(Issue("inventory_item_count", Common.UnavailableReason.NotRequested, "Loadout upkeep excludes inventory."));
            result.Issues.Add(Issue("carried_thing_id", Common.UnavailableReason.NotRequested, "Loadout upkeep excludes carried items."));
            return result;
        }

        // A loose candidate carries the supply ("allow-") token.
        private static Obs.GearItem Candidate(Thing thing, Common.ObservationContext context)
        {
            var row = Gear(thing);
            var snapshot = NativeSupplyAllow.Snapshot(thing, context);
            if (snapshot != null) row.ThingSnapshot = snapshot;
            return row;
        }

        internal static void Biocode(Thing thing, Obs.GearItem row)
        {
            var comp = thing.TryGetComp<CompBiocodable>();
            row.Biocoded = comp?.Biocoded == true;
            if (row.Biocoded && comp?.CodedPawn != null) row.BiocodedTo = Id(comp.CodedPawn.GetUniqueLoadID());
        }

        // Planning census, not simulated damage: natural armor or strongest worn layer.
        internal static double? RaidArmor(Map map)
        {
            var hostiles = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer));
            var total = 0.0;
            var count = 0;
            foreach (var hostile in hostiles) {
                total += PawnArmor(hostile);
                count++;
            }
            return count == 0 ? (double?)null : Number(total / count);
        }

        // One pawn's sharp armor: natural armor or its strongest worn layer.
        internal static double PawnArmor(Pawn pawn)
        {
            var armor = Number(pawn.GetStatValue(StatDefOf.ArmorRating_Sharp));
            if (pawn.apparel != null)
                foreach (var apparel in pawn.apparel.WornApparel)
                    armor = Math.Max(armor, Number(apparel.GetStatValue(StatDefOf.ArmorRating_Sharp)));
            return armor;
        }

        private static Obs.GearItem Gear(Thing thing)
        {
            var row = new Obs.GearItem { Thing = NativeRef.Thing(thing), Weapon = thing.def.IsWeapon,
                Ranged = thing.def.IsRangedWeapon, Melee = thing.def.IsMeleeWeapon,
                ArmorSharp = Number(thing.GetStatValue(StatDefOf.ArmorRating_Sharp)), ArmorBlunt = Number(thing.GetStatValue(StatDefOf.ArmorRating_Blunt)),
                InsulationCold = Number(thing.GetStatValue(StatDefOf.Insulation_Cold)), InsulationHeat = Number(thing.GetStatValue(StatDefOf.Insulation_Heat)) };
            Biocode(thing, row);
            if (thing.Stuff != null) row.Stuff = Id(thing.Stuff.defName);
            var range = NativePawnDetails.WeaponRange(thing); if (range.HasValue) row.Range = range.Value;
            if (thing.TryGetQuality(out var quality)) row.Quality = NativeEnums.Quality(quality);
            if (thing.def.useHitPoints) {
                row.HitPoints = thing.HitPoints; row.MaxHitPoints = thing.MaxHitPoints;
                row.ConditionFraction = Number((double)thing.HitPoints / thing.MaxHitPoints);
            }
            return row;
        }
    }
}
