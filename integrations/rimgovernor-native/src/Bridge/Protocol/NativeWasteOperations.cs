#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // GiveJobIntent job HaulWaste (#940, #1351): order one undrafted colonist to
    // haul one exposed waste item (spoiled, a rotting corpse, or one the
    // caller declares unwanted) to a separated dirty outdoor stockpile or an
    // empty grave, with the job the game's own Hauling WorkGiver builds.
    // Native checks the pawn, the item's protection and the destination
    // live; a pawn already hauling the item applies again. Applied means
    // ordered, not delivered; the next waste census reads where it is.
    internal static class NativeWasteOperations
    {
        private static HashSet<string> Set(IEnumerable<string> ids) => new HashSet<string>(ids, StringComparer.Ordinal);

        // Ports HomeWasteTools.Protection.
        private static string? Protection(Thing thing, bool burialAllowed)
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

        // Ports HomeWasteTools.Kind.
        private static string? Kind(Thing thing, HashSet<string> unwanted)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (thing is Corpse && rot != null && rot.Stage != RotStage.Fresh) return "corpse";
            if (!(thing is Corpse) && rot != null && rot.Stage != RotStage.Fresh) return "spoiled";
            return unwanted.Contains(thing.GetUniqueLoadID()) ? "unwanted" : null;
        }

        // Ports HomeWasteTools.DirtyCell: a conservative separation contract,
        // not a claim that any outdoor dump is harmless.
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

        // Self-computed, self-checked CAS token; see the class remarks.
        internal static string Token(Common.Identity identity, Thing thing)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var corpse = thing as Corpse;
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(thing.GetUniqueLoadID()); writer.Write(thing.def.defName);
                    writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(thing.stackCount);
                    writer.Write(thing.IsForbidden(Faction.OfPlayer)); writer.Write(rot?.Stage.ToString() ?? "");
                    writer.Write(corpse?.InnerPawn?.Faction?.GetUniqueLoadID() ?? "");
                    writer.Write(corpse?.InnerPawn?.Name?.ToStringFull ?? "");
                }
                using (var hash = SHA256.Create())
                    return "waste-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // Ports HomeWasteTools.Haul's WorkGiver scan: relocation to a dirty
        // outdoor stockpile cell, or burial in an empty grave. Never splits
        // or merges the observed stack.
        private static Job? FindJob(Pawn pawn, Thing thing, bool burialRequested)
        {
            foreach (var giver in WorkTypeDefOf.Hauling.workGiversByPriority)
            {
                if (giver == null || !giver.directOrderable || !(giver.Worker is WorkGiver_Scanner scanner)) continue;
                bool claims = scanner.PotentialWorkThingRequest.Accepts(thing) || (scanner.PotentialWorkThingsGlobal(pawn)?.Contains(thing) ?? false);
                if (!claims || scanner.ShouldSkip(pawn, true) || !scanner.HasJobOnThing(pawn, thing, true)) continue;
                var job = scanner.JobOnThing(pawn, thing, true);
                if (job == null || job.targetA.Thing != thing) continue;
                var grave = job.targetB.Thing as Building_Grave;
                bool burial = grave != null && !grave.HasCorpse && thing is Corpse;
                bool relocation = !burialRequested && job.def == JobDefOf.HaulToCell && job.targetB.IsValid
                    && DirtyCell(pawn.Map, job.targetB.Cell)
                    && (job.targetB.Cell.GetZone(pawn.Map) as Zone_Stockpile)?.GetStoreSettings().AllowedToAccept(thing) == true;
                if (relocation && (job.count < thing.stackCount || job.targetB.Cell.GetThingList(pawn.Map).Any(t => t.def == thing.def))) continue;
                if (!burial && !relocation) continue;
                if (!pawn.CanReach(job.targetB, PathEndMode.Touch, Danger.None)) continue;
                job.workGiverDef = giver;
                return job;
            }
            return null;
        }

        private static bool Hauling(Pawn pawn, Thing thing) =>
            pawn.carryTracker?.CarriedThing == thing || pawn.CurJob != null && pawn.CurJob.targetA.Thing == thing
                && (pawn.CurJob.def == JobDefOf.HaulToCell || pawn.CurJob.def == JobDefOf.HaulToContainer);

        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Thing? thing)
        {
            thing = null;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var observed);
            if (failure != null) return failure;
            var map = ProtoBoundary.LoadedMap(context);
            var foundPawn = pawn!;
            if (!foundPawn.IsColonist || observed!.Drafted || !observed.Eligible)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Waste haul requires an eligible undrafted pawn.");
            var carried = foundPawn.carryTracker?.CarriedThing;
            var foundThing = carried != null && RefIndex.Is(carried, intent.TargetId) ? carried
                : RefIndex.Thing(map, intent.TargetId);
            if (foundThing == null || foundThing.Destroyed) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact waste item is unavailable.");
            thing = foundThing;
            if (Hauling(foundPawn, thing)) return null;
            var unwanted = intent.Unwanted ? Set(new[] { intent.TargetId }) : Set(new string[0]);
            var protection = Protection(thing, false);
            if (protection != null || Kind(thing, unwanted) == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Item is protected or not eligible waste: " + (protection ?? "not eligible waste"));
            if (Stored(thing)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Already relocated; no further haul needed.");
            if (FindJob(foundPawn, thing, false) == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No native hauling job with an eligible separated storage or burial destination is available.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Thing thing, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = thing.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = false,
            }
        };

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Hauling(pawn!, thing!)) return Evidence(pawn!, thing!, pawn!.CurJob, false);
            var job = FindJob(pawn!, thing!, false) ?? throw new InvalidOperationException("No native waste job is available.");
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The pawn did not take the waste job.");
            return Evidence(pawn, thing!, job, true);
        }
    }
}
