#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The MeleeIntent arm of Actions/Apply (#856): a melee attack or a subdue,
    // validated live by the attack and subdue operations' own resolve checks
    // under the pawns' current snapshot tokens. An attack already running on
    // the target is kept, not reissued.
    internal static class NativeMeleeIntent
    {
        private sealed class Resolved
        {
            internal NativeControlIdentity Identity = null!;
            internal Pawn Pawn = null!;
            internal Thing Target = null!;
            internal NativePawnSnapshot Before = null!;
            internal JobDef? Definition;
        }

        private static string? Token(NativeControlIdentity identity, Common.ObservationContext context, Thing thing)
        {
            if (thing is Pawn pawn)
                return NativePawnControlState.Observe(identity, pawn, out var snapshot) == NativePawnControlResult.Ready ? snapshot?.Token : null;
            return NativeWasteOperations.Token(context.Identity, thing);
        }

        private static Common.Failure? Resolve(Operations.MeleeIntent? melee, Common.ObservationContext context, out Resolved? resolved)
        {
            resolved = null;
            if (melee == null || !melee.HasPawnId || !melee.HasTargetId || !ProtoBoundary.IsIdentifier(melee.PawnId) || !ProtoBoundary.IsIdentifier(melee.TargetId) || melee.PawnId == melee.TargetId)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A melee intent requires distinct pawn and target ids.");
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = ProtoBoundary.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            var pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == melee.PawnId);
            var player = Faction.OfPlayerSilentFail;
            var target = (Thing?)map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == melee.TargetId)
                ?? (player == null ? null : NativeObservationTools.HostileBuildings(map, player).SingleOrDefault(t => t.GetUniqueLoadID() == melee.TargetId));
            if (pawn == null || target == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The pawn or target is not spawned on this map.");
            var pawnToken = Token(identity, context, pawn);
            var targetToken = Token(identity, context, target);
            if (pawnToken == null || targetToken == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The pawn or target snapshot is unreadable.");
            var pawnRef = new Operations.EntityPrecondition { EntityId = melee.PawnId, ExpectedSnapshotToken = pawnToken };
            var targetRef = new Operations.EntityPrecondition { EntityId = melee.TargetId, ExpectedSnapshotToken = targetToken };
            if (melee.Subdue)
            {
                var order = new Operations.PawnTargetOrder { Pawn = pawnRef, Target = targetRef, Kind = Operations.PawnOrderKind.Subdue };
                if (!NativeSubdueOperations.Prepare(order, context, out identity, out var subduer, out var victim, out var snapshot, out var refused)) return refused;
                resolved = new Resolved { Identity = identity, Pawn = subduer!, Target = victim!, Before = snapshot! };
                return null;
            }
            var command = new Operations.AttackTarget { Pawn = pawnRef, Target = targetRef, Mode = Operations.AttackMode.Melee, RequireHostile = true, RequireStanding = true, RequireCombatHealth = true };
            if (!NativeCombatOperations.Resolve(command, context, out identity, out var attacker, out var resolvedTarget, out var before, out var definition, out _, out var failure)) return failure;
            if (!NativeMovementOperations.Owns(before!)) return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A melee attack requires an eligible drafted pawn with an owned draft claim.");
            resolved = new Resolved { Identity = identity, Pawn = attacker!, Target = resolvedTarget!, Before = before!, Definition = definition };
            return null;
        }

        private static bool Running(Pawn pawn, Thing target) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.AttackMelee && ReferenceEquals(pawn.CurJob.targetA.Thing, target);

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Thing target, bool issued) => new Receipts.EffectEvidence { Job = new Receipts.JobEffect {
            PawnId = pawn.GetUniqueLoadID(), JobDef = "AttackMelee", TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
            Issued = issued, Verified = Running(pawn, target), Drafted = pawn.Drafted,
            VerifiedReason = issued ? "Native melee job ordered." : "The melee job was already running; nothing issued." } };

        internal static Common.Failure? Validate(Operations.MeleeIntent melee, Common.ObservationContext context) => Resolve(melee, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.MeleeIntent melee, Common.ObservationContext context)
        {
            var failure = Resolve(melee, context, out var r);
            if (failure != null || r == null) throw new InvalidOperationException("Melee prerequisites changed before apply: " + failure?.Detail);
            if (Running(r.Pawn, r.Target)) return Evidence(r.Pawn, r.Target, false);
            bool accepted;
            if (melee.Subdue)
            {
                var victim = (Pawn)r.Target;
                NativeSubdueOperations.EnsureDrafted(r.Identity, r.Pawn, r.Before);
                accepted = NativeSubdueOperations.Take(r.Pawn, NativeSubdueOperations.MakeJob(r.Pawn, victim), victim);
            }
            else
            {
                var job = JobMaker.MakeJob(r.Definition, r.Target);
                job.killIncappedTarget = NativeCombatDamageRecord.Downed(r.Target);
                accepted = r.Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            if (!accepted) throw new InvalidOperationException("Native melee job was not taken.");
            return Evidence(r.Pawn, r.Target, true);
        }
    }

    internal sealed class MeleeActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeMeleeIntent.Validate(action.Melee, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeMeleeIntent.Apply(action.Melee, context);
    }
}
