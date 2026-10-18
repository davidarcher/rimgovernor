#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Defense-layout reads (B06c). Both are pure observations of the game's own
    // shooting and pathing models: cover fill and sight blocking are what
    // CoverUtility/GenSight consult, and edge reachability is the ordinary
    // walk-in raid approach (ground pathing to any map edge without opening
    // doors). Neither read reserves cells, previews placement or predicts
    // where a raid will actually path.
    internal static class NativeDefenseObservationTools
    {
        internal static Obs.DefenseSiteReply HandleReadDefenseSite(Obs.DefenseSiteRequest request)
        {
            if (!ValidateSite(request, out var failure0)) return new Obs.DefenseSiteReply { Outcome = failure0 };
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.DefenseSiteReply { Outcome = failure };
            try {
                var cells = Region(request.Region, map);
                if (cells == null) return new Obs.DefenseSiteReply {
                    Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region is outside the current map.") };
                return new Obs.DefenseSiteReply { Outcome = Site(map, cells, context) };
            }
            catch (Exception) { return new Obs.DefenseSiteReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native defense site facts could not be read completely.") }; }
        }

        internal static Obs.LinesOfFireReply HandleReadLinesOfFire(Obs.LinesOfFireRequest request)
        {
            if (!ValidateLines(request, out var failure0)) return new Obs.LinesOfFireReply { Outcome = failure0 };
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.LinesOfFireReply { Outcome = failure };
            try {
                var firing = request.FiringCells.Select(Native).ToList(); var approach = request.ApproachCells.Select(Native).ToList();
                if (firing.Concat(approach).Any(c => !c.InBounds(map))) return new Obs.LinesOfFireReply {
                    Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cell is outside the current map.") };
                return new Obs.LinesOfFireReply { Outcome = Lines(map, firing, approach, context, request.BudgetMs) };
            }
            catch (Exception) { return new Obs.LinesOfFireReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native lines of fire could not be read completely.") }; }
        }

        internal static bool ValidateSite(Obs.DefenseSiteRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and an inclusive rectangle are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            try { Region(request.Region, null); return true; } catch (Exception) { return false; }
        }
        internal static bool ValidateLines(Obs.LinesOfFireRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and unique, nonempty firing and approach cells are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            return Unique(request.FiringCells) && Unique(request.ApproachCells);
        }
        private static bool Unique(IEnumerable<Common.Cell> cells)
        {
            var list = cells.ToList();
            if (list.Count < 1 ||list.Any(c => !HasCell(c))) return false;
            return list.Select(Native).Distinct().Count() == list.Count;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null;
        private static IntVec3 Native(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        // Region is null when the rectangle leaves the live map, checked on its
        // corners before any cell is listed; a null map checks shape only.
        internal static List<IntVec3>? Region(Obs.Rectangle? region, Map? map)
        {
            var min = region?.Minimum; var max = region?.Maximum;
            if (!HasCell(min) || !HasCell(max) || max!.X < min!.X || max.Z < min.Z) throw new ArgumentException("Invalid rectangle.");
            if (map == null) return new List<IntVec3>();
            if (!new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return null;
            var width = (long)max.X - min.X + 1; var height = (long)max.Z - min.Z + 1;
            var result = new List<IntVec3>((int)(width * height));
            for (long z = min.Z; z <= max.Z; z++) for (long x = min.X; x <= max.X; x++) result.Add(new IntVec3((int)x, 0, (int)z));
            return result;
        }

        private static Obs.DefenseSiteSnapshot Site(Map map, List<IntVec3> cells, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            // Hostile walk-in raiders path on the ground and treat closed doors
            // as obstacles they must bash; reachability without opening doors is
            // the conservative "can arrive here from the edge" fact.
            var raider = TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly);
            // Rows are built once each and the array once per read: appending through `with` copied the whole
            // array per cell and cloned each row per field, the game thread's largest allocation per read.
            var rows = new List<Obs.DefenseCell>(cells.Count);
            foreach (var cell in cells) {
                var at = new Common.Cell { X = cell.x, Z = cell.z };
                if (cell.Fogged(map)) { rows.Add(new Obs.DefenseCell { Cell = at, Fogged = true }); continue; }
                var edifice = cell.GetEdifice(map);
                var cover = cell.GetCover(map);
                var giver = cover ?? (edifice != null && edifice.def.fillPercent > 0 ? edifice : null);
                var kind = giver != null ? NativeClearCover.KindOf(giver) : Obs.CoverKind.Unspecified;
                var covered = kind != Obs.CoverKind.Unspecified;
                var passable = !cell.Impassable(map);
                rows.Add(new Obs.DefenseCell {
                    Cell = at, Fogged = false,
                    Terrain = Identifier(cell.GetTerrain(map)?.defName),
                    FoundationRemovalDesignated = map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFoundation) != null,
                    Walkable = cell.Walkable(map), Passable = passable,
                    Roofed = cell.Roofed(map),
                    CoverFill = Finite(cover?.def.fillPercent ?? edifice?.def.fillPercent ?? 0),
                    CoverThingId = covered ? giver!.GetUniqueLoadID() : null,
                    CoverDefName = covered ? Identifier(giver!.def.defName) : null,
                    CoverKind = covered ? kind : null,
                    CoverDesignated = covered ? NativeClearCover.Designated(giver!) : null,
                    EdificeDefName = edifice != null ? Identifier(edifice.def.defName) : null,
                    EdificeStuffDefName = edifice?.Stuff != null ? Identifier(edifice.Stuff.defName) : null,
                    PlayerOwned = edifice != null && edifice.Faction == player,
                    NaturalRock = edifice != null && (edifice.def.building?.isNaturalRock == true || edifice.def.mineable),
                    Door = edifice is Building_Door,
                    EdgeReachable = passable && map.reachability.CanReachMapEdge(cell, raider),
                });
            }
            return new Obs.DefenseSiteSnapshot { Context = context,
                MapSize = new Obs.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) }, Cells = [.. rows] };
        }

        // The hostile lords RaidArrivalState has tracked since load, oldest first.
        internal static IEnumerable<Obs.RaidTrack> RaidTracks(Map map)
        {
            var raids = map.GetComponent<RaidArrivalState>();
            if (raids == null) yield break;
            foreach (var track in raids.Tracks.Values.OrderBy(t => t.SpawnTick).ThenBy(t => t.LordId)) {
                var row = new Obs.RaidTrack { LordId = "lord-" + track.LordId.ToString(System.Globalization.CultureInfo.InvariantCulture), FactionDef = Identifier(track.FactionDef),
                    SpawnTick = track.SpawnTick, Spawn = new Common.Cell { X = track.Spawn.x, Z = track.Spawn.z }, Ground = track.Ground, LastTick = track.LastTick };
                yield return row with { Trail = [.. track.Trail.Select(c => new Common.Cell { X = c.x, Z = c.z })] };
            }
        }

        internal static Obs.LinesOfFireSnapshot Lines(Map map, List<IntVec3> firing, List<IntVec3> approach, Common.ObservationContext context, uint? budgetMs)
        {
            var lines = new List<Obs.LineOfFire>();
            var budgetTicks = budgetMs.HasValue ? budgetMs.Value * System.Diagnostics.Stopwatch.Frequency / 1000 : long.MaxValue;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var from in firing) foreach (var to in approach) {
                // About 3 us a pair; the budget is checked per pair, so the snapshot holds a
                // firing-major prefix of the product and says so.
                if (System.Diagnostics.Stopwatch.GetTimestamp() - began >= budgetTicks) return new Obs.LinesOfFireSnapshot { Context = context, Lines = [.. lines], Skipped = true };
                var row = new Obs.LineOfFire { From = new Common.Cell { X = from.x, Z = from.z }, To = new Common.Cell { X = to.x, Z = to.z },
                    Distance = Finite((from - to).LengthHorizontal) };
                if (from.Fogged(map) || to.Fogged(map)) { lines.Add(row); continue; }
                lines.Add(row with { LineOfSight = GenSight.LineOfSight(from, to, map, true),
                    TargetCover = Finite(CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(to), from, map)),
                    ShooterCover = Finite(CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(from), to, map)) });
            }
            return new Obs.LinesOfFireSnapshot { Context = context, Lines = [.. lines] };
        }

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}
