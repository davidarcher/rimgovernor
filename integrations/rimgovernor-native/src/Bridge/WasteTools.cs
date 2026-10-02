#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeWasteTools
    {
        private static string Id(Thing thing) => thing.GetUniqueLoadID();

        private static string? Protection(Thing thing, bool burialAllowed = false)
        {
            if (!thing.Spawned || thing.Position.Fogged(thing.Map)) return "held_or_unobserved";
            if (thing.IsForbidden(Faction.OfPlayer)) return "player_forbidden";
            if (thing.questTags != null && thing.questTags.Count > 0) return "quest_item";
            if (thing.def.comps != null && thing.def.comps.Any(c => c is CompProperties_Dissolution
                || c is CompProperties_GasOnDamage || c is CompProperties_Explosive)) return "hazardous_item_requires_specialized_containment";
            if (thing is MinifiedThing || thing is Pawn || thing is Building) return "protected_possession";
            var corpse = thing as Corpse;
            if (corpse != null)
            {
                var inner = corpse.InnerPawn;
                if (inner == null) return "corpse_identity_unknown";
                if (!burialAllowed && (inner.Faction == Faction.OfPlayer || inner.Name != null)) return "named_or_colony_corpse";
                if (!burialAllowed && inner.RaceProps.Humanlike) return "human_corpse_requires_funeral_policy";
            }
            return null;
        }

        private static string? Kind(Thing thing, HashSet<string> unwanted)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (thing is Corpse && rot != null && rot.Stage != RotStage.Fresh) return "corpse";
            if (!(thing is Corpse) && rot != null && rot.Stage != RotStage.Fresh) return "spoiled";
            return unwanted.Count > 0 && unwanted.Contains(Id(thing)) ? "unwanted" : null;
        }

        // A conservative separation contract, not a claim that any outdoor dump is harmless.
        private static bool DirtyCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || cell.Fogged(map) || cell.Roofed(map)
                || map.areaManager.Home[cell] || cell.GetRoom(map)?.UsesOutdoorTemperature != true) return false;
            return !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(cell) < 144);
        }

        private static bool Stored(Thing thing)
        {
            var zone = thing.Position.GetZone(thing.Map) as Zone_Stockpile;
            return zone != null && zone.GetStoreSettings().AllowedToAccept(thing) && DirtyCell(thing.Map, thing.Position);
        }

        // CorpseOf is a corpse's inner pawn class (#832): colonist for the
        // player faction's humanlike, stranger for any other humanlike,
        // animal otherwise; null for anything that is not a corpse.
        internal static Common.CorpseClass? CorpseOf(Thing thing)
        {
            var inner = (thing as Corpse)?.InnerPawn;
            if (inner == null) return null;
            if (!inner.RaceProps.Humanlike) return Common.CorpseClass.Animal;
            return inner.Faction == Faction.OfPlayer ? Common.CorpseClass.Colonist : Common.CorpseClass.Stranger;
        }

        // Project is the waste census on the typed wire
        // (ColonyFactsSnapshot.waste): every visible waste candidate and
        // corpse, then every buried corpse at its grave's cell. More rows
        // than the 256 the Go contract admits leave the section unavailable.
        internal static Obs.WasteReply Project(Map map, Common.ObservationContext context)
        {
            var empty = new HashSet<string>();
            var items = new List<Obs.WasteItem>();
            // Filter before sorting (#1273): the stable sort of the few kept
            // rows orders them exactly as sorting every thing's load ID did,
            // without building and comparing a string per thing on the map.
            var kept = new List<KeyValuePair<Thing, string?>>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Position.Fogged(map)) continue;
                var kind = Kind(thing, empty);
                if (kind == null && !(thing is Corpse)) continue;
                kept.Add(new KeyValuePair<Thing, string?>(thing, kind));
            }
            foreach (var pair in kept.OrderBy(p => Id(p.Key)))
            {
                var thing = pair.Key; var kind = pair.Value;
                var protection = Protection(thing);
                var row = new Obs.WasteItem { Thing = NativeRef.Thing(thing), Count = thing.stackCount, Eligible = protection == null && kind != null,
                    State = protection == null && Stored(thing) ? Obs.WasteLocation.Relocated : Obs.WasteLocation.Exposed };
                if (kind != null) row.Kind = NativeEnums.Waste(kind);
                if (protection != null) row.ProtectedReason = protection;
                var rot = thing.TryGetComp<CompRottable>();
                if (rot != null) row.RotStage = NativeEnums.Rot(rot.Stage);
                var of = CorpseOf(thing);
                if (of != null) row.CorpseClass = of.Value;
                items.Add(row);
            }
            foreach (var grave in map.listerThings.AllThings.OfType<Building_Grave>().Where(g => !g.Position.Fogged(map)))
                foreach (var body in grave.GetDirectlyHeldThings().OfType<Corpse>())
                {
                    var row = new Obs.WasteItem { Thing = NativeRef.Thing(body), Count = 1, Kind = Obs.WasteKind.Corpse, ProtectedReason = "grave",
                        Eligible = false, State = Obs.WasteLocation.Buried, Grave = NativeRef.Of(Id(grave)) };
                    var of = CorpseOf(body);
                    if (of != null) row.CorpseClass = of.Value;
                    items.Add(row);
                }
            var snapshot = new Obs.WasteSnapshot { Context = context.Clone()};
            snapshot.Items.AddRange(items);
            return new Obs.WasteReply { Observed = snapshot };
        }
    }
}
