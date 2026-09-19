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

        private static Obs.GearItem Gear(Thing thing)
        {
            var row = new Obs.GearItem { Thing = Entity(thing), Weapon = thing.def.IsWeapon, Apparel = thing.def.IsApparel,
                Ranged = thing.def.IsRangedWeapon, Melee = thing.def.IsMeleeWeapon,
                ArmorSharp = Number(thing.GetStatValue(StatDefOf.ArmorRating_Sharp)), ArmorBlunt = Number(thing.GetStatValue(StatDefOf.ArmorRating_Blunt)),
                InsulationCold = Number(thing.GetStatValue(StatDefOf.Insulation_Cold)), InsulationHeat = Number(thing.GetStatValue(StatDefOf.Insulation_Heat)) };
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
