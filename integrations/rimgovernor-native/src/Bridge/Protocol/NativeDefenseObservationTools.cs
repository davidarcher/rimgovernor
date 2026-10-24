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
using RimGovernor.Brain.Bridge.Contracts;
using Common = RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
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
        // A refused request throws NativeRefusedException; facts that cannot be read completely throw an
        // unavailable fault.
        internal static Obs.DefenseSiteSnapshot HandleReadDefenseSite(Obs.DefenseSiteRequest request)
        {
            if (!ValidateSite(request, out var failure0)) throw NativeFaults.Refused(failure0);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            var cells = Region(request.Region, map) ?? throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region is outside the current map."));
            try { return Site(map, cells, context); }
            catch (Exception error) { throw NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.ReadFailed, "Native defense site facts could not be read completely: " + error.GetType().Name + ": " + error.Message)); }
        }

        internal static Obs.LinesOfFireSnapshot HandleReadLinesOfFire(Obs.LinesOfFireRequest request)
        {
            if (!ValidateLines(request, out var failure0)) throw NativeFaults.Refused(failure0);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            var firing = request.FiringCells.Select(Native).ToList(); var approach = request.ApproachCells.Select(Native).ToList();
            if (firing.Concat(approach).Any(c => !c.InBounds(map)))
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cell is outside the current map."));
            try { return Lines(map, firing, approach, context, request.BudgetMs); }
            catch (Exception error) { throw NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.ReadFailed, "Native lines of fire could not be read completely: " + error.GetType().Name + ": " + error.Message)); }
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
                var at = new DomainCell(cell.x, cell.z);
                if (cell.Fogged(map)) { rows.Add(new Obs.DefenseCell.Fogged { Cell = at }); continue; }
                var edifice = cell.GetEdifice(map);
                var cover = cell.GetCover(map);
                var giver = cover ?? (edifice != null && edifice.def.fillPercent > 0 ? edifice : null);
                var kind = giver != null ? NativeClearCover.KindOf(giver) : null;
                var passable = !cell.Impassable(map);
                rows.Add(new Obs.DefenseCell.Seen {
                    Cell = at,
                    Terrain = Identifier(cell.GetTerrain(map)?.defName),
                    FoundationRemovalDesignated = map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFoundation) != null,
                    Walkable = cell.Walkable(map), Passable = passable,
                    Roofed = cell.Roofed(map),
                    CoverFill = Finite(cover?.def.fillPercent ?? edifice?.def.fillPercent ?? 0),
                    Cover = kind is { } k ? new Obs.DefenseCover { ThingId = giver!.GetUniqueLoadID(), DefName = Identifier(giver.def.defName), Kind = k, Designated = NativeClearCover.Designated(giver) } : null,
                    Edifice = edifice == null ? null : new Obs.DefenseEdifice {
                        DefName = Identifier(edifice.def.defName),
                        Stuff = edifice.Stuff != null ? Identifier(edifice.Stuff.defName) : null,
                        PlayerOwned = edifice.Faction == player,
                        NaturalRock = edifice.def.building?.isNaturalRock == true || edifice.def.mineable,
                        Door = edifice is Building_Door,
                    },
                    EdgeReachable = passable && map.reachability.CanReachMapEdge(cell, raider),
                });
            }
            return new Obs.DefenseSiteSnapshot { Context = context, Cells = [.. rows] };
        }

        // The hostile lords RaidArrivalState has tracked since load, oldest first.
        internal static IEnumerable<Obs.RaidTrack> RaidTracks(Map map)
        {
            var raids = map.GetComponent<RaidArrivalState>();
            if (raids == null) yield break;
            foreach (var track in raids.Tracks.Values.OrderBy(t => t.SpawnTick).ThenBy(t => t.LordId)) {
                yield return new Obs.RaidTrack { LordId = "lord-" + track.LordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SpawnTick = new RimGovernor.Brain.Domain.Tick(track.SpawnTick), Spawn = new RimGovernor.Brain.Domain.Cell(track.Spawn.x, track.Spawn.z), Ground = track.Ground,
                    Trail = [.. track.Trail.Select(c => new RimGovernor.Brain.Domain.Cell(c.x, c.z))] };
            }
        }

        internal static Obs.LinesOfFireSnapshot Lines(Map map, List<IntVec3> firing, List<IntVec3> approach, Common.ObservationContext context, uint budgetMs)
        {
            var lines = new List<Obs.LineOfFire>();
            var budgetTicks = budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var from in firing) foreach (var to in approach) {
                // About 3 us a pair; the budget is checked per pair, so the snapshot holds a
                // firing-major prefix of the product and says so.
                if (System.Diagnostics.Stopwatch.GetTimestamp() - began >= budgetTicks) return new Obs.LinesOfFireSnapshot { Context = context, Lines = [.. lines], Skipped = true };
                var a = new DomainCell(from.x, from.z); var b = new DomainCell(to.x, to.z);
                var distance = Finite((from - to).LengthHorizontal);
                if (from.Fogged(map) || to.Fogged(map)) { lines.Add(new Obs.LineOfFire.Fogged { From = a, To = b, Distance = distance }); continue; }
                lines.Add(new Obs.LineOfFire.Traced { From = a, To = b, Distance = distance,
                    LineOfSight = GenSight.LineOfSight(from, to, map, true) });
            }
            return new Obs.LinesOfFireSnapshot { Context = context, Lines = [.. lines], Skipped = false };
        }

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}
