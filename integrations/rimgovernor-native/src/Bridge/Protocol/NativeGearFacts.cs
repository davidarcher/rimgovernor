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
        // The most candidates one loadout carries: the best by gain, then by
        // thing id. MaintainEquipment only ever wears the best funded one.
        internal const int CandidateBound = 8;

        internal static Obs.GearSnapshot Read(Map map, Common.ObservationContext context, int limit)
        {
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            Require(people.Count, limit);
            var result = new Obs.GearSnapshot { Context = context.Clone(), Completeness = Complete(people.Count) };
            ReadClimate(map, result);
            var stored = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>()
                .Where(a => a.IsInValidStorage() && !a.IsForbidden(Faction.OfPlayer) && !a.WornByCorpse
                    && (!a.def.useHitPoints || (float)a.HitPoints / a.MaxHitPoints > .5f))
                .GroupBy(a => new { Def = a.def.defName, Stuff = a.Stuff?.defName ?? "",
                    Quality = a.TryGetQuality(out var q) ? (int)q : 2,
                    Band = a.def.useHitPoints ? Math.Min(9, (int)(10f * a.HitPoints / a.MaxHitPoints)) : 9 })
                .OrderBy(g => g.Key.Def).ThenBy(g => g.Key.Stuff).ThenBy(g => g.Key.Quality).ThenBy(g => g.Key.Band).ToList();
            Require(stored.Count, 4096);
            result.StoredApparel = new Obs.GearStorage { Completeness = Complete(stored.Count) };
            foreach (var group in stored)
                result.StoredApparel.Rows.Add(new Obs.GearStock { DefName = group.Key.Def, Stuff = group.Key.Stuff,
                    Quality = group.Key.Quality, HpBand = group.Key.Band, Count = group.Sum(a => a.stackCount) });
            // ImproveGear (NativeGearOperations) checks the pawn's control
            // snapshot token and each candidate's supply token, so the census
            // carries both the way the pawn and supply censuses do (issue #233).
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
                var omitted = 0;
                if (refusal == null) {
                    // Every eligible loose item scores for every pawn, so on
                    // a map strewn with raid apparel the census grew with
                    // pawns x items (~700 JSON bytes per candidate) and put
                    // the routine colony facts at the 1 MiB envelope (issue
                    // #320). Only the CandidateBound best by gain are carried;
                    // the rest count as filtered, never as unmatched.
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
                    var bound = Math.Min(CandidateBound, limit);
                    omitted = Math.Max(0, candidates.Count - bound);
                    foreach (var candidate in candidates.OrderByDescending(c => c.Value).ThenBy(c => c.Key.thingIDNumber).Take(bound))
                        row.Candidates.Add(new Obs.GearCandidate { Item = Candidate(candidate.Key, context), Gain = Number(candidate.Value) });
                }
                var needs = GearUpkeepTools.ProductionNeeds(pawn);
                Require(needs.Count, limit);
                foreach (var need in needs) {
                    var replacement = new Obs.GearReplacementNeed { DefName = Id(need.defName), Reason = Text(need.reason) };
                    if (need.stuff != null) replacement.Stuff = Id(need.stuff);
                    row.ReplacementNeeds.Add(replacement);
                }
                row.Completeness = Complete(row.Candidates.Count, omitted);
                result.Pawns.Add(row);
            }
            return result;
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
                Require(pawn.equipment.AllEquipmentListForReading.Count);
                foreach (var thing in pawn.equipment.AllEquipmentListForReading) result.Equipped.Add(Gear(thing));
                result.Armed = pawn.equipment.Primary != null;
                if (pawn.equipment.Primary != null) result.PrimaryId = Id(pawn.equipment.Primary.GetUniqueLoadID());
                else result.Issues.Add(Issue("primary_id", Common.UnavailableReason.NotApplicable, "No equipped primary weapon."));
            }
            if (pawn.apparel == null) result.Issues.Add(Issue("apparel", Common.UnavailableReason.ReadFailed, "No apparel tracker."));
            else {
                Require(pawn.apparel.WornApparel.Count);
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

        // A loose candidate carries the supply ("allow-") token ImproveGear
        // and the equip order check against the exact thing.
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
                var armor = Number(hostile.GetStatValue(StatDefOf.ArmorRating_Sharp));
                if (hostile.apparel != null)
                    foreach (var apparel in hostile.apparel.WornApparel)
                        armor = Math.Max(armor, Number(apparel.GetStatValue(StatDefOf.ArmorRating_Sharp)));
                total += armor;
                count++;
            }
            return count == 0 ? (double?)null : Number(total / count);
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
                Require(thing.def.apparel.layers.Count); Require(thing.def.apparel.bodyPartGroups.Count);
                row.ApparelLayers.Add(thing.def.apparel.layers.Select(d => Id(d.defName)));
                row.BodyPartGroups.Add(thing.def.apparel.bodyPartGroups.Select(d => Id(d.defName)));
            }
            return row;
        }
    }
}
