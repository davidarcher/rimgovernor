#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Mirror = RimGovernor.Protocol.Mirror;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One thing on a cell as the grid read holds it: the mirror's
    /// Thing with its strings still strings, so reads compare against the
    /// keyframe's without a string table. Encode turns it into the wire
    /// message. Fractions are held to 1/100 so a growing plant or rotting
    /// corpse does not re-send its cell every rare tick.
    /// </summary>
    internal sealed class ThingRec : IEquatable<ThingRec>
    {
        internal string Def = "";
        internal Mirror.ThingCategory Category;
        internal Mirror.ThingFaction Faction;
        internal uint Flags;
        internal ulong Id;
        internal uint Count;
        // Plant
        internal float Growth;
        internal bool Blighted;
        // Corpse
        internal Mirror.CorpseClass Class;
        internal float Rot;
        // Filth
        internal uint Thickness;
        // Item
        internal float Deterioration;
        // Building (blueprints and frames too)
        internal uint HitPoints;
        internal bool Burning, Reserved;
        internal (string Def, uint Count)[] Needed = Array.Empty<(string, uint)>();
        internal string[] Casket = Array.Empty<string>();

        public bool Equals(ThingRec? o) => o != null && Def == o.Def && Category == o.Category && Faction == o.Faction && Flags == o.Flags && Id == o.Id
            && Count == o.Count && Growth == o.Growth && Blighted == o.Blighted && Class == o.Class && Rot == o.Rot && Thickness == o.Thickness
            && Deterioration == o.Deterioration && HitPoints == o.HitPoints && Burning == o.Burning && Reserved == o.Reserved
            && Needed.SequenceEqual(o.Needed) && Casket.SequenceEqual(o.Casket);
        public override bool Equals(object? obj) => Equals(obj as ThingRec);
        public override int GetHashCode() => (int)Id;

        internal static bool SameList(ThingRec[]? a, ThingRec[]? b)
        {
            var n = a?.Length ?? 0;
            if (n != (b?.Length ?? 0)) return false;
            for (int i = 0; i < n; i++) if (!a![i].Equals(b![i])) return false;
            return true;
        }

        /// The wire thing, its strings interned through put.
        internal Mirror.Thing Wire(Func<string, uint> put)
        {
            var t = new Mirror.Thing { Def = put(Def), Category = Category, Faction = Faction, Flags = Flags, Id = Id, Count = Count };
            switch (Category)
            {
                case Mirror.ThingCategory.Plant: t.Plant = new Mirror.PlantState { Growth = Growth, Blighted = Blighted }; break;
                case Mirror.ThingCategory.Corpse: t.Corpse = new Mirror.CorpseState { Class = Class, Rot = Rot }; break;
                case Mirror.ThingCategory.Filth: t.Filth = new Mirror.FilthState { Thickness = Thickness }; break;
                case Mirror.ThingCategory.Item: t.Item = new Mirror.ItemState { Deterioration = Deterioration }; break;
                case Mirror.ThingCategory.Building:
                    var b = new Mirror.BuildingState { HitPoints = HitPoints, Burning = Burning, Reserved = Reserved };
                    foreach (var need in Needed) b.Needed.Add(new Mirror.MaterialNeed { Def = put(need.Def), Count = need.Count });
                    foreach (var held in Casket) b.Casket.Add(put(held));
                    t.Building = b;
                    break;
            }
            return t;
        }
    }

    /// <summary>
    /// Reads the things on a map's cells for CellGridEncoder. One
    /// instance serves one read: it caches the per-thing record (a building
    /// is listed on each cell it covers) and the map-wide sets (designated,
    /// reserved things, ancient-temple triggers) a per-thing query would
    /// otherwise rescan.
    /// </summary>
    internal sealed class ThingReader
    {
        private readonly Map map;
        private readonly Faction? player;
        private readonly Dictionary<Thing, ThingRec?> records = new Dictionary<Thing, ThingRec?>();
        private readonly HashSet<Thing> designatedThings = new HashSet<Thing>();
        private readonly HashSet<IntVec3> designatedCells = new HashSet<IntVec3>();
        private readonly HashSet<Thing> reserved = new HashSet<Thing>();
        private List<RectTrigger>? triggers;

        internal ThingReader(Map map, Faction? player)
        {
            this.map = map;
            this.player = player;
            foreach (var d in map.designationManager.AllDesignations)
                if (d.target.HasThing) designatedThings.Add(d.target.Thing); else designatedCells.Add(d.target.Cell);
            foreach (var r in map.reservationManager.ReservationsReadOnly)
                if (r.Target.HasThing) reserved.Add(r.Target.Thing);
        }

        /// The cell's things in native order, null for none. Pawns, motes
        /// and projectiles are not listed.
        internal ThingRec[]? At(IntVec3 cell)
        {
            var list = map.thingGrid.ThingsListAtFast(cell);
            List<ThingRec>? found = null;
            for (int i = 0; i < list.Count; i++)
            {
                var thing = list[i];
                if (!thing.Spawned) continue;
                if (!records.TryGetValue(thing, out var rec))
                {
                    rec = Record(thing, cell);
                    records[thing] = rec;
                }
                if (rec != null) (found ??= new List<ThingRec>()).Add(rec);
            }
            return found?.ToArray();
        }

        private static float Q(float x) => (float)Math.Round(Math.Max(0f, Math.Min(1f, float.IsNaN(x) ? 0f : x)) * 100f) / 100f;
        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");

        private ThingRec? Record(Thing thing, IntVec3 cell)
        {
            var def = thing.def;
            if (thing is Pawn || def.category == ThingCategory.Pawn || def.category == ThingCategory.Mote || def.category == ThingCategory.Projectile) return null;
            var rec = new ThingRec { Id = (ulong)Math.Max(0, thing.thingIDNumber), Count = (uint)Math.Max(1, thing.stackCount) };
            var constructible = thing is Blueprint || thing is Frame;
            // A blueprint or frame is listed as what it builds.
            rec.Def = Identifier(constructible && def.entityDefToBuild != null ? def.entityDefToBuild.defName : def.defName);
            var building = thing as Building;
            var flags = 0u;
            if (map.edificeGrid[cell] == thing) flags |= 1;                      // EDIFICE
            if (thing is Blueprint) flags |= 2;                                  // BLUEPRINT
            if (thing is Frame) flags |= 4;                                      // FRAME
            if (def.passability == Traversability.Impassable) flags |= 8;        // IMPASSABLE
            if (def.holdsRoof) flags |= 16;                                      // HOLDS_ROOF
            if (building != null && player != null)
            {
                if (building.DeconstructibleBy(player).Accepted) flags |= 32;    // DECONSTRUCTIBLE
                if (def.Minifiable) flags |= 64;                                 // MINIFIABLE
                if (building.ClaimableBy(player).Accepted) flags |= 128;         // CLAIMABLE
                if ((flags & 32) != 0 && building.Faction != player && !def.building.isNaturalRock && !def.mineable
                    && NativeClearanceObservationTools.AncientDanger(map, building, player, triggers ??= NativeClearanceObservationTools.TempleTriggers(map)))
                    flags |= 2048;                                               // ANCIENT_DANGER
            }
            if (designatedThings.Contains(thing) || (map.edificeGrid[cell] == thing && designatedCells.Contains(cell))) flags |= 256; // DESIGNATED
            if (thing.TryGetComp<CompForbiddable>()?.Forbidden == true) flags |= 512; // FORBIDDEN
            if (def.EverHaulable) flags |= 1024;                                 // HAULABLE
            if (building != null && def.building != null && def.building.isNaturalRock) flags |= 4096; // NATURAL_ROCK
            rec.Flags = flags;
            rec.Faction = thing.Faction == null ? Mirror.ThingFaction.None
                : thing.Faction == player ? Mirror.ThingFaction.Player
                : player != null && thing.Faction.HostileTo(player) ? Mirror.ThingFaction.Hostile : Mirror.ThingFaction.Neutral;

            if (building != null || constructible)
            {
                rec.Category = Mirror.ThingCategory.Building;
                rec.HitPoints = (uint)Math.Max(0, thing.HitPoints);
                rec.Burning = thing.IsBurning();
                rec.Reserved = reserved.Contains(thing);
                if (thing is IConstructible owed)
                    rec.Needed = owed.TotalMaterialCost().Select(m => (m.thingDef, need: owed.ThingCountNeeded(m.thingDef))).Where(m => m.need > 0).Select(m => (Identifier(m.thingDef.defName), (uint)m.need)).ToArray();
                if (thing is Building_Casket casket)
                    rec.Casket = casket.GetDirectlyHeldThings().Select(t => Identifier(t.def.defName)).ToArray();
            }
            else if (thing is Corpse corpse)
            {
                rec.Category = Mirror.ThingCategory.Corpse;
                var race = corpse.InnerPawn?.RaceProps;
                rec.Class = race == null ? Mirror.CorpseClass.Other : race.Humanlike ? Mirror.CorpseClass.Humanlike : race.Animal ? Mirror.CorpseClass.Animal : Mirror.CorpseClass.Other;
                rec.Rot = Q(corpse.GetComp<CompRottable>()?.RotProgressPct ?? 0f);
            }
            else if (thing is Filth filth)
            {
                rec.Category = Mirror.ThingCategory.Filth;
                rec.Thickness = (uint)Math.Max(0, filth.thickness);
            }
            else if (thing is Plant plant)
            {
                rec.Category = Mirror.ThingCategory.Plant;
                rec.Growth = Q(plant.Growth);
                rec.Blighted = plant.Blighted;
            }
            else if (def.category == ThingCategory.Item)
            {
                rec.Category = Mirror.ThingCategory.Item;
                rec.Deterioration = def.useHitPoints && thing.MaxHitPoints > 0 ? Q(1f - (float)thing.HitPoints / thing.MaxHitPoints) : 0f;
            }
            else rec.Category = Mirror.ThingCategory.Other;
            return rec;
        }
    }
}
