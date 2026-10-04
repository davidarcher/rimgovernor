#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeMineAcquisition
    {
        internal static string Token(Common.Identity identity, string id, string resource, int x, int z, int hitPoints, int yield, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(id); writer.Write(resource); writer.Write(x); writer.Write(z); writer.Write(hitPoints); writer.Write(yield); writer.Write(designated); }
                using (var hash = SHA256.Create()) return "mine-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static Obs.SnapshotRef Snapshot(Mineable rock, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = rock.GetUniqueLoadID(), Token = Token(context.Identity, rock.GetUniqueLoadID(),
                rock.def.building.mineableThing.defName, rock.Position.x, rock.Position.z, rock.HitPoints, rock.def.building.mineableYield, ResourceAcquisitionTools.Designated(rock)) };
        internal const string Kind = "Mine";
        // Miner is the colonist rule ResourceAcquisitionTools.Eligible
        // applies to mining: someone must be able to do the work now.
        private static bool Miner(Pawn p, Mineable rock) => !p.Downed && !p.Drafted && !p.InMentalState
            && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !rock.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(rock, PathEndMode.Touch, Danger.None);
        // Prepare is the apply-time precondition list for mine
        // (action-contracts.md): ResourceAcquisitionTools.Eligible plus the
        // request's cell, resource and designation rules, one rule at a time;
        // the excavation-geometry rule reports MiningBlocker's own text.
        internal static bool Prepare(AcquireRequest command, Common.ObservationContext context, out Mineable? rock, out Common.Failure failure)
        {
            rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mining requires an exact safe mineable snapshot, an eligible miner and safe excavation geometry.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = RefIndex.Thing<Mineable>(map, command.SourceId);
            var blocker = found == null ? null : ResourceAcquisitionTools.MiningBlocker(found, map);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned, "the exact rock is no longer spawned on this map")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the rock is not at the expected cell")
                .Require(() => found!.def.building.mineableThing?.defName == command.ResourceDefName, "the rock no longer yields the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the rock's cell is fogged")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the rock is forbidden")
                .Require(() => blocker == null, "excavation geometry is unsafe: " + blocker)
                .Require(() => !ResourceAcquisitionTools.Designated(found!), "the rock is already designated for mining")
                .Require(() => new Designator_Mine().CanDesignateThing(found!).Accepted, "the native mine designator refuses the rock")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Miner(p, found!)), "no free colonist able to mine can reach the rock");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            rock = found;
            return true;
        }
        // Guard opens the acquisition-guarded Mine record of a rock about to
        // be designated: the guard re-checks MiningBlocker before each pick
        // and follows a mined cell that opens protected space with a wall.
        internal static void Guard(Mineable rock) => NativeDesignationGuards.Add(new GuardedDesignation {
            Id = "designation-" + Guid.NewGuid().ToString("N"), Guard = GuardNames.Acquisition, Designation = DesignationDefOf.Mine.defName,
            ExpectedDef = rock.def.defName, MapId = rock.Map.uniqueID, X = rock.Position.x, Z = rock.Position.z });
    }
}
