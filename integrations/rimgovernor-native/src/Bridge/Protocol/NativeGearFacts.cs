#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
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
            result.FinishedResearch.Add(DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => r.IsFinished)
                .Select(r => Id(r.defName)).OrderBy(n => n, StringComparer.Ordinal));
            var catalog = Catalog();
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
            // The census carries the pawn's control snapshot token and each
            // candidate's supply token the way the pawn and supply censuses
            // do (issue #233); the WEAR pawn order (#939) checks neither.
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            foreach (var pawn in people) {
                var refusal = GearUpkeepTools.Available(pawn);
                var row = new Obs.GearLoadout {
                    Pawn = Entity(pawn), Deficit = GearUpkeepTools.Deficit(pawn),
                    Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = Id(pawn.GetUniqueLoadID()), Token = GearUpkeepTools.Identity(pawn) },
                    ComfortableMinC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                    ApparelPolicy = pawn.outfits?.CurrentApparelPolicy == null ? null : NativeApparelPolicyOperations.Read(pawn),
                    Equipment = Equipment(pawn)
                };
                if (refusal != null) row.Blocker = Text(refusal);
                if (NativePawnControlState.Observe(identity, pawn, out var control) == NativePawnControlResult.Ready && control != null)
                    row.Pawn.Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = control.PawnId, Token = control.Token };
                if (refusal == null) {
                    // Every eligible loose item is offered, best gain first
                    // (issue #769: a bound offered every pawn the same few shirts).
                    // Candidates are apparel only: they feed the wear order
                    // (NativeGearOperations, JobDefOf.Wear), which looks its
                    // target up among loose apparel. Loose weapons are the
                    // equip family's (PAWN_ORDER_KIND_EQUIP); listing them
                    // here had a WoodLog admitted as apparel wear and refused
                    // on every attempt (issue #339).
                    var candidates = new List<KeyValuePair<Thing, float>>();
                    foreach (var apparel in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>()) {
                        if (GearUpkeepTools.Eligible(pawn, apparel) != null) continue;
                        var gain = GearUpkeepTools.Gain(pawn, apparel);
                        if (gain >= .05f) candidates.Add(new KeyValuePair<Thing, float>(apparel, gain));
                    }
                    foreach (var candidate in candidates.OrderByDescending(c => c.Value).ThenBy(c => c.Key.thingIDNumber))
                        row.Candidates.Add(new Obs.GearCandidate { Item = Candidate(candidate.Key, context), Gain = Number(candidate.Value) });
                }
                var needs = GearUpkeepTools.ProductionNeeds(pawn);
                foreach (var need in needs) {
                    var replacement = new Obs.GearReplacementNeed { DefName = Id(need.defName), Reason = Text(need.reason) };
                    if (need.stuff != null) replacement.Stuff = Id(need.stuff);
                    row.ReplacementNeeds.Add(replacement);
                }
                row.LoadoutModel = Model(map, pawn, row, catalog);
                result.Pawns.Add(row);
            }
            return result;
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

        private static Obs.GearLoadoutModel Model(Map map, Pawn pawn, Obs.GearLoadout row, List<RecipeDef> catalog)
        {
            var model = new Obs.GearLoadoutModel { Female = pawn.gender == Gender.Female };
            if (pawn.story?.traits != null)
                foreach (var trait in pawn.story.traits.allTraits) model.Traits.Add(new Obs.Trait { DefName = Id(trait.def.defName), Degree = trait.Degree });
            if (pawn.apparel != null)
                foreach (var worn in pawn.apparel.WornApparel) {
                    var option = Physical(worn, "worn");
                    option.Locked = pawn.apparel.IsLocked(worn) || pawn.outfits?.forcedHandler.AllowedToAutomaticallyDrop(worn) == false;
                    model.Worn.Add(option);
                }
            var things = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().ToDictionary(a => a.GetUniqueLoadID());
            foreach (var candidate in row.Candidates.Take(ModelPhysical))
                if (things.TryGetValue(candidate.Item.Thing.Id, out var apparel))
                    model.Options.Add(Physical(apparel, apparel.IsInValidStorage() ? "stored" : "loose"));
            foreach (var recipe in catalog) {
                if (model.Options.Count >= ModelOptions) break;
                var def = recipe.ProducedThingDef;
                if (!def.apparel.CorrectGenderForWearing(pawn.gender) || !def.apparel.developmentalStageFilter.Has(pawn.DevelopmentalStage)
                    || !ApparelUtility.HasPartsToWear(pawn, def)) continue;
                // One stuff per definition: the allowed stuff with the most stock.
                var stuff = def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def).OrderByDescending(s => map.resourceCounter.GetCount(s))
                    .ThenBy(s => s.defName, StringComparer.Ordinal).FirstOrDefault() : null;
                if (def.MadeFromStuff && stuff == null) continue;
                var option = Option("bill:" + def.defName + "/" + (stuff?.defName ?? ""), def, stuff, "bill");
                var research = new List<ResearchProjectDef>();
                if (recipe.researchPrerequisite != null) research.Add(recipe.researchPrerequisite);
                if (recipe.researchPrerequisites != null) research.AddRange(recipe.researchPrerequisites);
                option.Research.Add(research.Select(r => Id(r.defName)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
                foreach (var cost in def.CostListAdjusted(stuff, false))
                    option.Ingredients.Add(new Obs.Quantity { DefName = Id(cost.thingDef.defName), Units = cost.count });
                model.Options.Add(option);
            }
            return model;
        }

        private static Obs.GearLoadoutOption Physical(Apparel apparel, string source)
        {
            var row = Option(apparel.GetUniqueLoadID(), apparel.def, apparel.Stuff, source);
            if (apparel.TryGetQuality(out var quality)) row.Quality = (int)quality;
            if (apparel.def.useHitPoints) row.Condition = Number(Math.Min(1.0, (double)apparel.HitPoints / apparel.MaxHitPoints));
            row.Tainted = apparel.WornByCorpse;
            return row;
        }

        // Normal-quality def x stuff stats before condition; negative stats clamp to zero.
        private static Obs.GearLoadoutOption Option(string id, ThingDef def, ThingDef? stuff, string source)
        {
            double Stat(StatDef stat) => Math.Max(0, Number(def.GetStatValueAbstract(stat, stuff)));
            var offsets = def.equippedStatOffsets;
            var row = new Obs.GearLoadoutOption { Id = Id(id), DefName = Id(def.defName), Source = source, Quality = (int)QualityCategory.Normal,
                Condition = 1, ArmorSharp = Stat(StatDefOf.ArmorRating_Sharp), ArmorBlunt = Stat(StatDefOf.ArmorRating_Blunt),
                InsulationCold = Stat(StatDefOf.Insulation_Cold), InsulationHeat = Stat(StatDefOf.Insulation_Heat),
                MarketValue = Stat(StatDefOf.MarketValue),
                MoveSpeed = offsets == null ? 0 : Number(offsets.GetStatOffsetFromList(StatDefOf.MoveSpeed)),
                Psychic = offsets != null && offsets.GetStatOffsetFromList(StatDefOf.PsychicSensitivity) < 0,
                Shield = def.HasComp(typeof(CompShield)),
                Smokepop = def.Verbs?.Any(v => v.verbClass == typeof(Verb_SmokePop)) == true };
            if (stuff != null) row.Stuff = Id(stuff.defName);
            if (def.apparel != null) {
                row.ApparelLayers.Add(def.apparel.layers.Select(d => Id(d.defName)).Distinct());
                row.BodyPartGroups.Add(def.apparel.bodyPartGroups.Select(d => Id(d.defName)).Distinct());
            }
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
            if (snapshot != null) row.Thing.Snapshot = snapshot;
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
            var row = new Obs.GearItem { Thing = Entity(thing), Weapon = thing.def.IsWeapon, Apparel = thing.def.IsApparel,
                Ranged = thing.def.IsRangedWeapon, Melee = thing.def.IsMeleeWeapon,
                ArmorSharp = Number(thing.GetStatValue(StatDefOf.ArmorRating_Sharp)), ArmorBlunt = Number(thing.GetStatValue(StatDefOf.ArmorRating_Blunt)),
                InsulationCold = Number(thing.GetStatValue(StatDefOf.Insulation_Cold)), InsulationHeat = Number(thing.GetStatValue(StatDefOf.Insulation_Heat)) };
            Biocode(thing, row);
            if (thing.Stuff != null) row.Stuff = Id(thing.Stuff.defName);
            var range = NativePawnDetails.WeaponRange(thing); if (range.HasValue) row.Range = range.Value;
            if (thing.TryGetQuality(out var quality)) row.Quality = quality.ToString();
            if (thing.def.useHitPoints) {
                row.HitPoints = thing.HitPoints; row.MaxHitPoints = thing.MaxHitPoints;
                row.ConditionFraction = Number((double)thing.HitPoints / thing.MaxHitPoints);
            }
            if (thing.def.apparel != null) {
                row.ApparelLayers.Add(thing.def.apparel.layers.Select(d => Id(d.defName)));
                row.BodyPartGroups.Add(thing.def.apparel.bodyPartGroups.Select(d => Id(d.defName)));
            }
            return row;
        }
    }
}
