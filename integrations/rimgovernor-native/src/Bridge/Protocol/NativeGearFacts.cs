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
        internal static Obs.GearSnapshot Read(Map map, Common.ObservationContext context, int limit)
        {
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            Require(people.Count, limit);
            var result = new Obs.GearSnapshot { Context = context.Clone(), Completeness = Complete(people.Count) };
            foreach (var pawn in people) {
                var refusal = GearUpkeepTools.Available(pawn);
                var row = new Obs.GearLoadout {
                    Pawn = Entity(pawn), Deficit = GearUpkeepTools.Deficit(pawn),
                    Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = Id(pawn.GetUniqueLoadID()), Token = GearUpkeepTools.Identity(pawn) },
                    ComfortableMinC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                    Equipment = Equipment(pawn)
                };
                if (refusal != null) row.Blocker = Text(refusal);
                if (refusal == null) {
                    foreach (var apparel in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().OrderBy(a => a.thingIDNumber)) {
                        if (GearUpkeepTools.Eligible(pawn, apparel) != null) continue;
                        var gain = GearUpkeepTools.Gain(pawn, apparel);
                        if (gain >= .05f) row.Candidates.Add(new Obs.GearCandidate { Item = Gear(apparel), Gain = Number(gain) });
                    }
                    foreach (var weapon in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).OfType<ThingWithComps>().OrderBy(w => w.thingIDNumber))
                        if (GearUpkeepTools.WeaponEligible(pawn, weapon) == null)
                            row.Candidates.Add(new Obs.GearCandidate { Item = Gear(weapon), Gain = Number(GearUpkeepTools.WeaponGain(pawn, weapon)) });
                }
                Require(row.Candidates.Count, limit);
                var needs = GearUpkeepTools.ProductionNeeds(pawn);
                Require(needs.Count, limit);
                foreach (var need in needs) {
                    var replacement = new Obs.GearReplacementNeed { DefName = Id(need.defName), Reason = Text(need.reason) };
                    if (need.stuff != null) replacement.Stuff = Id(need.stuff);
                    row.ReplacementNeeds.Add(replacement);
                }
                row.Completeness = Complete(row.Candidates.Count);
                result.Pawns.Add(row);
            }
            return result;
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

        private static Obs.GearItem Gear(Thing thing)
        {
            var row = new Obs.GearItem { Thing = Entity(thing), Weapon = thing.def.IsWeapon, Apparel = thing.def.IsApparel,
                Ranged = thing.def.IsRangedWeapon, Melee = thing.def.IsMeleeWeapon,
                ArmorSharp = Number(thing.GetStatValue(StatDefOf.ArmorRating_Sharp)), ArmorBlunt = Number(thing.GetStatValue(StatDefOf.ArmorRating_Blunt)),
                InsulationCold = Number(thing.GetStatValue(StatDefOf.Insulation_Cold)), InsulationHeat = Number(thing.GetStatValue(StatDefOf.Insulation_Heat)) };
            if (thing.Stuff != null) row.Stuff = Id(thing.Stuff.defName);
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
