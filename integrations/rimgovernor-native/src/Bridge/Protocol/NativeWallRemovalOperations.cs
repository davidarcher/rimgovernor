#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The wall_upgrade guard's resolver: a DECONSTRUCT
    // Designate on a wall cell admits one native deconstruct designation
    // held by the wall_upgrade guard (WallUpgradeSafety.Check), which the
    // designation-guard hooks keep re-checking against the site's enclosure,
    // supports and roof support until the pawn finishes. The intent names
    // the wall's cell, so the site is resolved here
    // from the ListWallUpgradeSites census:
    //  1. a straight replacement site whose backup cells all hold same-stuff
    //     stone walls (demolish the original);
    //  2. a cleanup site where this wall is a completed backup of a standing
    //     stone permanent wall (clear the backup);
    //  3. a corner site with open salvage access, rebuilt from whichever
    //     stone material the site lists (Go budgets the stock).
    // Several admissible sites of one class refuse rather than guess. Applied
    // means designated; Go reads the building census for the wall's removal.
    internal static class NativeWallRemovalOperations
    {
        private sealed class Candidate
        {
            internal WallRemovalRecord Record = null!;
            internal Obs.WallUpgradeSite Site = null!;
            internal Func<Obs.WallUpgradeSite?> Reread = null!;
        }

        [ThreadStatic] private static string? lastBlocker;

        private static Candidate? Try(WallRemovalRecord record, Obs.WallUpgradeSite? site, Func<Obs.WallUpgradeSite?> reread)
        {
            if (site == null) return null;
            // A standing demolition designation no record claims is no veto:
            // admission adopts it under this ledger's record.
            if (site.HasBlocker) { lastBlocker = site.Blocker; return null; }
            var candidate = new Candidate { Record = record, Site = site, Reread = reread };
            var blocker = WallUpgradeSafety.Prepare(record);
            if (blocker != null) lastBlocker = blocker;
            return blocker == null ? candidate : null;
        }

        // Every admissible site of one wall, best class first.
        private static List<List<Candidate>> Resolve(Map map, Building wall, Common.ObservationContext context)
        {
            var id = wall.GetUniqueLoadID();
            var materials = NativeWallUpgradeObservationTools.StonyStuffs();
            var straight = new List<Candidate>(); var cleanup = new List<Candidate>(); var corner = new List<Candidate>();
            foreach (var normal in WallUpgradeSafety.Directions)
            {
                var n = normal;
                Func<Obs.WallUpgradeSite?> reread = () => NativeWallUpgradeObservationTools.Replacement(map, wall, n, context);
                var site = reread();
                if (site == null) continue;
                var left = site.LeftSupport.Id; var right = site.RightSupport.Id;
                if (!WallUpgradeSafety.Corner(normal))
                {
                    if (site.CompletedBackups.Count != site.BackupCells.Count || site.BackupCells.Count == 0) continue;
                    var record = WallUpgradeSafety.NewRecord(map, id, id, left, right, site.CompletedBackups.Select(b => b.Id), "", "",
                        wall.Position.x, wall.Position.z, normal.x, normal.z);
                    if (Try(record, site, reread) is Candidate found) straight.Add(found);
                    continue;
                }
                foreach (var material in materials)
                {
                    var record = WallUpgradeSafety.NewRecord(map, id, id, left, right, Enumerable.Empty<string>(), "", material.defName,
                        wall.Position.x, wall.Position.z, normal.x, normal.z);
                    if (Try(record, site, reread) is Candidate found) { corner.Add(found); break; }
                }
            }
            if (WallUpgradeSafety.Stone(wall))
                foreach (var normal in WallUpgradeSafety.Directions.Where(d => !WallUpgradeSafety.Corner(d)))
                {
                    var side = new IntVec3(-normal.z, 0, normal.x);
                    for (var k = -1; k <= 1; k++)
                    {
                        var origin = wall.Position - normal - side * k;
                        var permanent = NativeWallUpgradeObservationTools.ColonistWall(map, origin);
                        if (permanent == null || !WallUpgradeSafety.Stone(permanent) || permanent.Stuff != wall.Stuff) continue;
                        var n = normal; var p = permanent;
                        Func<Obs.WallUpgradeSite?> reread = () => NativeWallUpgradeObservationTools.Cleanup(map, p, n, context);
                        var site = reread();
                        if (site == null || !site.CompletedBackups.Any(b => b.Id == id)) continue;
                        var record = WallUpgradeSafety.NewRecord(map, id, permanent.GetUniqueLoadID(), site.LeftSupport.Id, site.RightSupport.Id,
                            site.CompletedBackups.Select(b => b.Id), permanent.GetUniqueLoadID(), "", origin.x, origin.z, normal.x, normal.z);
                        if (Try(record, site, reread) is Candidate found) cleanup.Add(found);
                    }
                }
            return new List<List<Candidate>> { straight, cleanup, corner };
        }

        // The apply-time precondition list for a wall_upgrade Designate. wall is null
        // when no colonist wall stands at the cell (already removed: applies
        // again); pending is the ledger's open removal of the wall, which
        // applies again with its evidence; otherwise candidate is the one
        // admissible site.
        private static string? Refusal(Operations.DesignateIntent intent, Common.ObservationContext context,
            out Building? wall, out GuardedDesignation? pending, out Candidate? candidate, out Common.FailureCode code)
        {
            wall = null; pending = null; candidate = null; code = Common.FailureCode.InvalidRequest;
            var expected = intent.Target?.Id;
            if (intent.Cell == null || !intent.Cell.HasX || !intent.Cell.HasZ || intent.Target != null && !ProtoBoundary.IsIdentifier(expected))
                return "The wall_upgrade guard requires one wall cell.";
            if (intent.Designation != Operations.ThingDesignation.Deconstruct || intent.ReplaceWithWall || intent.HasExpectedDef || intent.HasThingId)
                return "The wall_upgrade guard takes DECONSTRUCT on a cell and an optional target only.";
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return "Loaded map required.";
            var cell = new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
            if (!cell.InBounds(map)) return "Wall cell is outside the map.";
            wall = NativeWallUpgradeObservationTools.ColonistWall(map, cell);
            if (wall == null) return null;
            if (expected != null && wall.GetUniqueLoadID() != expected)
            { code = Common.FailureCode.StaleIdentity; return "A different wall stands at the cell."; }
            pending = WallUpgradeSafety.Pending(wall);
            if (pending != null) return null;
            lastBlocker = null;
            var classes = Resolve(map, wall, context).FirstOrDefault(c => c.Count > 0);
            if (classes == null)
                return lastBlocker ?? "No wall-upgrade site: completed same-stuff stone backups, a standing stone permanent wall or open corner access is required.";
            if (classes.Count > 1) return "Several admissible wall-upgrade sites share this wall; the request cannot name one.";
            candidate = classes[0];
            return null;
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _, out _, out var code);
            return refusal == null ? null : ProtoBoundary.Fail(code, refusal);
        }

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out var wall, out var pending, out var candidate, out _);
            if (refusal != null) throw new InvalidOperationException("Wall removal prerequisites changed before apply: " + refusal);
            if (wall == null)
                return new Receipts.EffectEvidence { Wall = new Receipts.WallEffect { TargetId = intent.Target?.Id ?? "", DemolitionObserved = false } };
            var id = wall.GetUniqueLoadID();
            if (pending != null)
                return new Receipts.EffectEvidence { Wall = new Receipts.WallEffect { TargetId = id, RemovalId = pending.Id, DemolitionObserved = false } };
            var chosen = candidate!;
            var before = chosen.Site.Snapshot?.Token ?? "";
            if (WallUpgradeSafety.Prepare(chosen.Record) != null) throw new InvalidOperationException("Wall removal site changed before designation.");
            var blocker = WallUpgradeSafety.Commit(chosen.Record);
            if (blocker != null) throw new InvalidOperationException(blocker);
            var effect = new Receipts.WallEffect { TargetId = id, RemovalId = chosen.Record.Id, DemolitionObserved = false,
                Site = new Receipts.SnapshotEvidence { EntityId = id, BeforeToken = before } };
            var after = chosen.Reread();
            if (after?.Snapshot != null) effect.Site.AfterToken = after.Snapshot.Token;
            return new Receipts.EffectEvidence { Wall = effect };
        }
    }
}
