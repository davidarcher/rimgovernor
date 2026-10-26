using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Native
{
    /// <summary>
    /// Reads the things on a map's cells for CellGridReader as the policy's thing records. One instance serves one
    /// read: it caches the per-thing record (a building is listed on each cell it covers) and the map-wide sets
    /// (designated, reserved things, ancient-temple triggers) a per-thing query would otherwise rescan. A whole-map
    /// read also keeps the map's last whole read: a thing whose facts are unchanged keeps its record, and a cell whose
    /// records are all kept keeps its array, so a steady map allocates no thing records. Records and arrays are never
    /// modified once read. Game thread only.
    /// </summary>
    internal sealed class ThingReader
    {
        // The last whole-map read of each map, and the dictionary the next one fills.
        private sealed class Memo
        {
            internal Dictionary<Thing, P.Thing> Last = new Dictionary<Thing, P.Thing>(), Spare = new Dictionary<Thing, P.Thing>();
            internal P.Thing[]?[] Cells = Array.Empty<P.Thing[]?>();
        }

        private static readonly ConditionalWeakTable<Map, Memo> Memos = new ConditionalWeakTable<Map, Memo>();
        private static readonly Dictionary<Thing, P.Thing> None = new Dictionary<Thing, P.Thing>();
        private static readonly P.PlantState NoPlant = new P.PlantState();
        private static readonly P.CorpseState NoCorpse = new P.CorpseState();

        private readonly Map map;
        private readonly Faction? player;
        private readonly Memo? memo;
        private readonly Dictionary<Thing, P.Thing> records;
        private readonly Dictionary<Thing, P.Thing> previous;
        private readonly P.Thing[]?[] previousCells;
        private readonly List<P.Thing> scratch = new List<P.Thing>();
        private readonly HashSet<Thing> designatedThings = new HashSet<Thing>();
        private readonly HashSet<IntVec3> designatedCells = new HashSet<IntVec3>();
        private readonly HashSet<IntVec3> foundationRemovals = new HashSet<IntVec3>();
        private readonly HashSet<Thing> reserved = new HashSet<Thing>();
        private List<RectTrigger>? triggers;
        private List<Hive>? hives;

        /// A reader for a whole-map read, against the map's last whole read.
        internal static ThingReader Whole(Map map)
        {
            if (!Memos.TryGetValue(map, out var memo))
            {
                memo = new Memo();
                Memos.Add(map, memo);
            }
            return new ThingReader(map, memo);
        }

        /// A reader for a read of part of the map: no record carries over.
        internal ThingReader(Map map) : this(map, null)
        {
        }

        private ThingReader(Map map, Memo? memo)
        {
            this.map = map;
            this.memo = memo;
            player = Faction.OfPlayerSilentFail;
            if (memo != null)
            {
                records = memo.Spare;
                records.Clear();
                previous = memo.Last;
                previousCells = memo.Cells.Length == map.Size.x * map.Size.z ? memo.Cells : Array.Empty<P.Thing[]?>();
            }
            else
            {
                records = new Dictionary<Thing, P.Thing>();
                previous = None;
                previousCells = Array.Empty<P.Thing[]?>();
            }
            foreach (var d in map.designationManager.AllDesignations)
            {
                if (d.target.HasThing) designatedThings.Add(d.target.Thing); else designatedCells.Add(d.target.Cell);
                if (!d.target.HasThing && d.def == DesignationDefOf.RemoveFoundation) foundationRemovals.Add(d.target.Cell);
            }
            foreach (var r in map.reservationManager.ReservationsReadOnly)
                if (r.Target.HasThing) reserved.Add(r.Target.Thing);
        }

        /// A RemoveFoundation designation stands on the cell.
        internal bool FoundationRemoval(IntVec3 cell) => foundationRemovals.Contains(cell);

        /// The cell's things in native order, null for none; j is the cell's index in a whole-map read. Pawns, motes
        /// and projectiles are not listed.
        internal P.Thing[]? At(IntVec3 cell, int j)
        {
            var list = map.thingGrid.ThingsListAtFast(cell);
            scratch.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var thing = list[i];
                var category = thing.def.category;
                if (!thing.Spawned || thing is Pawn || category == ThingCategory.Pawn || category == ThingCategory.Mote || category == ThingCategory.Projectile) continue;
                if (!records.TryGetValue(thing, out var rec))
                {
                    rec = Record(thing, cell);
                    records[thing] = rec;
                }
                scratch.Add(rec);
            }
            if (scratch.Count == 0) return null;
            var last = j < previousCells.Length ? previousCells[j] : null;
            if (last != null && last.Length == scratch.Count)
            {
                var same = true;
                for (int i = 0; i < last.Length && same; i++) same = ReferenceEquals(last[i], scratch[i]);
                if (same) return last;
            }
            return scratch.ToArray();
        }

        /// Ends a whole-map read: its records and cell arrays become the map's last read.
        internal void Finish(P.Thing[]?[] cells)
        {
            if (memo == null) return;
            previous.Clear();
            memo.Spare = previous;
            memo.Last = records;
            memo.Cells = cells;
        }

        private static float Q(float x) => (float)Math.Round(Math.Max(0f, Math.Min(1f, float.IsNaN(x) ? 0f : x)) * 100f) / 100f;
        private static string Identifier(string? value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");

        private P.Thing Record(Thing thing, IntVec3 cell)
        {
            var def = thing.def;
            var id = (ulong)Math.Max(0, thing.thingIDNumber);
            var count = (uint)Math.Max(1, thing.stackCount);
            var constructible = thing is Blueprint || thing is Frame;
            // A blueprint or frame is listed as what it builds.
            var name = Identifier(constructible && def.entityDefToBuild != null ? def.entityDefToBuild.defName : def.defName);
            var building = thing as Verse.Building;
            var flags = P.ThingMarks.None;
            if (map.edificeGrid[cell] == thing) flags |= P.ThingMarks.Edifice;
            if (thing is Blueprint) flags |= P.ThingMarks.Blueprint;
            if (thing is Frame) flags |= P.ThingMarks.Frame;
            if (def.passability == Traversability.Impassable) flags |= P.ThingMarks.Impassable;
            if (def.holdsRoof) flags |= P.ThingMarks.HoldsRoof;
            if (building != null && player != null)
            {
                if (building.DeconstructibleBy(player).Accepted) flags |= P.ThingMarks.Deconstructible;
                if (def.Minifiable) flags |= P.ThingMarks.Minifiable;
                if (building.ClaimableBy(player).Accepted) flags |= P.ThingMarks.Claimable;
                if ((flags & P.ThingMarks.Deconstructible) != 0 && building.Faction != player && !def.building.isNaturalRock && !def.mineable
                    && NativeClearanceObservationTools.AncientDanger(map, building, player, triggers ??= NativeClearanceObservationTools.TempleTriggers(map),
                        hives ??= map.listerThings.GetThingsOfType<Hive>().ToList()))
                    flags |= P.ThingMarks.AncientDanger;
            }
            if (designatedThings.Contains(thing) || (map.edificeGrid[cell] == thing && designatedCells.Contains(cell))) flags |= P.ThingMarks.Designated;
            if (thing.TryGetComp<CompForbiddable>()?.Forbidden == true) flags |= P.ThingMarks.Forbidden;
            if (def.EverHaulable) flags |= P.ThingMarks.Haulable;
            if (building != null && def.building != null && def.building.isNaturalRock) flags |= P.ThingMarks.NaturalRock;
            var faction = thing.Faction == null ? P.ThingFaction.None
                : thing.Faction == player ? P.ThingFaction.Player
                : player != null && thing.Faction.HostileTo(player) ? P.ThingFaction.Hostile : P.ThingFaction.Neutral;

            P.ThingCategory category;
            float growth = 0f, rot = 0f, deterioration = 0f;
            bool blighted = false, burning = false, isReserved = false;
            var corpseClass = P.CorpseClass.Other;
            uint thickness = 0, hitPoints = 0;
            EquatableArray<P.Material> needed = default;
            EquatableArray<string> casket = default;
            var stuff = "";
            if (building != null || constructible)
            {
                category = P.ThingCategory.Building;
                // A blueprint or frame lists the stuff it builds with, as it lists the def it builds.
                var made = (thing as IConstructible)?.EntityToBuildStuff() ?? thing.Stuff;
                if (made != null) stuff = Identifier(made.defName);
                hitPoints = (uint)Math.Max(0, thing.HitPoints);
                burning = thing.IsBurning();
                isReserved = reserved.Contains(thing);
                if (thing is IConstructible owed)
                    needed = EquatableArray.From(owed.TotalMaterialCost().Select(m => (m.thingDef, need: owed.ThingCountNeeded(m.thingDef))).Where(m => m.need > 0).Select(m => new P.Material(Identifier(m.thingDef.defName), (uint)m.need)));
                if (thing is Building_Casket holder)
                    casket = EquatableArray.From(holder.GetDirectlyHeldThings().Select(t => Identifier(t.def.defName)));
            }
            else if (thing is Corpse corpse)
            {
                category = P.ThingCategory.Corpse;
                var race = corpse.InnerPawn?.RaceProps;
                corpseClass = race == null ? P.CorpseClass.Other : race.Humanlike ? P.CorpseClass.Humanlike : race.Animal ? P.CorpseClass.Animal : P.CorpseClass.Other;
                rot = Q(corpse.GetComp<CompRottable>()?.RotProgressPct ?? 0f);
            }
            else if (thing is Filth filth)
            {
                category = P.ThingCategory.Filth;
                thickness = (uint)Math.Max(0, filth.thickness);
            }
            else if (thing is Plant plant)
            {
                category = P.ThingCategory.Plant;
                growth = Q(plant.Growth);
                blighted = plant.Blighted;
            }
            else if (def.category == ThingCategory.Item)
            {
                category = P.ThingCategory.Item;
                deterioration = def.useHitPoints && thing.MaxHitPoints > 0 ? Q(1f - (float)thing.HitPoints / thing.MaxHitPoints) : 0f;
            }
            else category = P.ThingCategory.Other;

            // The last read's record when every fact is unchanged.
            if (previous.TryGetValue(thing, out var old) && string.Equals(old.Def, name, StringComparison.Ordinal) && old.Category == category
                && old.Faction == faction && old.Flags == flags && old.Id == id && old.Count == count
                && old.Plant.Growth == growth && old.Plant.Blighted == blighted && old.Corpse.Class == corpseClass && old.Corpse.Rot == rot
                && old.FilthThickness == thickness && old.ItemDeterioration == deterioration
                && (category == P.ThingCategory.Building
                    ? old.Building is { } b && b.HitPoints == hitPoints && string.Equals(b.Stuff, stuff, StringComparison.Ordinal) && b.Burning == burning && b.Reserved == isReserved && b.Needed == needed && b.Casket == casket
                    : old.Building == null))
                return old;
            return new P.Thing
            {
                Def = name, Category = category, Faction = faction, Flags = flags, Id = id, Count = count,
                Plant = category == P.ThingCategory.Plant ? new P.PlantState { Growth = growth, Blighted = blighted } : NoPlant,
                Corpse = category == P.ThingCategory.Corpse ? new P.CorpseState { Class = corpseClass, Rot = rot } : NoCorpse,
                FilthThickness = thickness, ItemDeterioration = deterioration,
                Building = category == P.ThingCategory.Building ? new P.BuildingState { HitPoints = hitPoints, Stuff = stuff, Burning = burning, Reserved = isReserved, Needed = needed, Casket = casket } : null,
            };
        }
    }
}
