#nullable enable
using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The game-thread half of the planning-window view (#650): builds a
    /// candidate root of detached cell values in bands of ChunkRows rows.
    /// A band of the published root whose every cell the change grid
    /// (CellTracking) shows unchanged since the band's capture is carried
    /// over, revalidated at this tick, without reading its cells again;
    /// every other band is read as get_cells reads the planning fields,
    /// under the same visibility and absence rules. The candidate is
    /// published by the worker that projects it.
    /// </summary>
    internal static class PlanningWindowViewCapture
    {
        internal const int MaxCells = NativeObservationTools.CellsPageLimit;
        internal const int ChunkRows = 8;

        /// The bundle's view section (#650), on the game thread: the
        /// candidate its encoder publishes and serves, timed into the hop's
        /// observation account with the cells actually read.
        internal static PlanningViewRoot? ForBundle(Map map, Obs.BundleRequest request, Common.ObservationContext context)
        {
            if (request.PlanningWindowView == null) return null;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var root = Capture(map, request.PlanningWindowView, context, out var captured, out var candidates);
            ObservationWork.Captured("planningWindowView", System.Diagnostics.Stopwatch.GetTimestamp() - began, captured, candidates);
            return root;
        }

        /// The candidate root, or null when the region is invalid for map
        /// or the cells could not be read. captured counts the cells read,
        /// candidates the region's cells.
        internal static PlanningViewRoot? Capture(Map map, Obs.BundlePlanningWindowViewRequest request, Common.ObservationContext context, out int captured, out long candidates)
        {
            captured = 0; candidates = 0;
            var min = request.Region?.Minimum; var max = request.Region?.Maximum;
            if (min == null || max == null || !min.HasX || !min.HasZ || !max.HasX || !max.HasZ || max.X < min.X || max.Z < min.Z) return null;
            candidates = ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1);
            if (candidates > MaxCells || !new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return null;
            Install();
            var publisher = PlanningWindowViewPublisher.Shared;
            var ticket = publisher.Begin(context.Identity);
            var held = publisher.Acquire();
            if (held != null && (held.Incarnation != ticket.Incarnation || !held.Serves(context.Identity, min.X, min.Z, max.X, max.Z, PlanningViewRoot.PlanningMask))) held = null;
            var tick = context.Tick;
            try
            {
                var tracking = CellTracking.For(map);
                var bands = (max.Z - min.Z) / ChunkRows + 1;
                var chunks = new PlanningViewChunk[bands];
                for (var i = 0; i < bands; i++)
                {
                    var minZ = min.Z + i * ChunkRows; var maxZ = Math.Min(minZ + ChunkRows - 1, max.Z);
                    var old = held != null && i < held.ChunkCount ? held.Chunk(i) : null;
                    if (old != null && old.MinZ == minZ && old.MaxZ == maxZ && Unchanged(map, tracking, min.X, max.X, old))
                    {
                        chunks[i] = old.Revalidated(tick);
                        continue;
                    }
                    var cells = new PlanningViewCell[(maxZ - minZ + 1) * (max.X - min.X + 1)];
                    var next = 0;
                    for (var z = minZ; z <= maxZ; z++)
                        for (var x = min.X; x <= max.X; x++) cells[next++] = Read(map, tracking, new IntVec3(x, 0, z));
                    captured += cells.Length;
                    chunks[i] = new PlanningViewChunk(minZ, maxZ, ticket.Revision, tick, tick, cells);
                }
                return new PlanningViewRoot(context.Identity, context.NativeGeneration, ticket, map.Size.x, map.Size.z, min.X, min.Z, max.X, max.Z,
                    PlanningViewRoot.PlanningMask, tick, chunks);
            }
            catch (Exception) { return null; }
        }

        // Every cell is checked, not the first changed one: both checks
        // stamp a cell whose indoors, pollution or glow moved, so a later
        // changed_since_tick reader sees the change this read noticed.
        private static bool Unchanged(Map map, CellTracking tracking, int minX, int maxX, PlanningViewChunk chunk)
        {
            var unchanged = true;
            for (var z = chunk.MinZ; z <= chunk.MaxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    unchanged &= tracking.Unchanged(cell, chunk.CapturedTick) & tracking.GrowthUnchanged(cell);
                }
            return unchanged;
        }

        // The planning fields of one cell, as NativeObservationTools.ReadCells
        // emits them: a fogged cell carries only its fog.
        private static PlanningViewCell Read(Map map, CellTracking tracking, IntVec3 cell)
        {
            if (cell.Fogged(map)) return PlanningViewCell.Fog;
            var flags = 0;
            string? roofName = null, zoneId = null, roomId = null;
            var roof = cell.GetRoof(map);
            if (roof != null) roofName = Identifier(roof.defName);
            if (cell.Walkable(map)) flags |= PlanningViewCell.Walkable;
            if (!cell.Impassable(map)) flags |= PlanningViewCell.Passable;
            if (NativeObservationTools.CellOccupied(map, cell)) flags |= PlanningViewCell.Occupied;
            if (NativeObservationTools.CellDoorway(map, cell)) flags |= PlanningViewCell.Doorway;
            if (cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)) flags |= PlanningViewCell.SupportsLight;
            var zone = map.zoneManager.ZoneAt(cell);
            if (zone != null) zoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (NativeZoneCreation.StorageEmpty(cell, map)) flags |= PlanningViewCell.StorageEmpty;
            var room = cell.GetRoom(map);
            tracking.NoteRoom(cell, room);
            if (room != null) roomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (CellTracking.Indoors(room)) flags |= PlanningViewCell.Indoors;
            var polluted = ModsConfig.BiotechActive && map.pollutionGrid.IsPolluted(cell);
            if (polluted) flags |= PlanningViewCell.Polluted;
            var groundGlow = map.glowGrid.GroundGlowAt(cell);
            var glow = Finite(groundGlow);
            tracking.NoteGrowth(cell, polluted, (float)glow);
            var fertility = map.fertilityGrid.FertilityAt(cell);
            double fertile = 0;
            if (fertility > 0f) { fertile = Finite(fertility); flags |= PlanningViewCell.HasFertility; }
            return new PlanningViewCell(false, flags, roofName, zoneId, roomId, glow, fertile);
        }

        private static bool installed;

        // Game.Dispose runs on every return to the menu and before every
        // load of another save: the view and its pending captures end there.
        private static void Install()
        {
            if (installed) return;
            installed = true;
            new Harmony("rimgovernor.planning-window-view").Patch(AccessTools.Method(typeof(Game), nameof(Game.Dispose), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(PlanningWindowViewCapture), nameof(Unloaded)));
        }

        private static void Unloaded() => PlanningWindowViewPublisher.Shared.Invalidate();

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
