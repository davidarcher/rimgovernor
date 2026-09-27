#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // CombatOrders (#850): one batch of combat micro orders applied in request
    // order inside one call, so on one game tick. Validation reuses the attack
    // (NativeCombatOperations.Legal), movement (NativeMovementOperations.Legal)
    // and owned-draft (NativeMovementOperations.Owns) rules. Per-order
    // refusals are results, not call failures. The receipt certifies the
    // orders were taken; observing the fight is the caller's next read.
    internal static class NativeCombatOrders
    {
        internal const int MaximumOrders = 64;

        internal static bool Valid(Operations.CombatOrders? command)
        {
            if (command == null || command.Orders.Count < 1 || command.Orders.Count > MaximumOrders) return false;
            foreach (var order in command.Orders)
            {
                if (order == null) return false;
                bool door = order.OrderCase == Operations.CombatOrder.OrderOneofCase.Door;
                if (door ? order.Pawn != null : !NativeDraftProtocol.ValidEntityTokenOptional(order.Pawn)) return false;
                switch (order.OrderCase)
                {
                    case Operations.CombatOrder.OrderOneofCase.Move: if (!ValidCell(order.Move)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.AttackGround: if (!ValidCell(order.AttackGround)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Attack:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(order.Attack) || order.Attack.EntityId == order.Pawn!.EntityId) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.FireMode:
                        if (order.FireMode != Operations.CombatFireMode.AtWill && order.FireMode != Operations.CombatFireMode.Hold) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Door:
                        if (!ValidCell(order.Door.Cell) || !order.Door.HasMode
                            || order.Door.Mode != Operations.CombatDoorMode.HoldOpen && order.Door.Mode != Operations.CombatDoorMode.Close) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.HoldPosition:
                    case Operations.CombatOrder.OrderOneofCase.Stop: break;
                    default: return false;
                }
            }
            return true;
        }

        private static bool ValidCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0;

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.CombatOrders; var pre = request.Precondition;
            if (!Valid(command)) return Refuse(Common.FailureCode.InvalidRequest, "Combat orders need 1.." + MaximumOrders + " orders, each an exact pawn (none for a door) and one supported order with explicit cells and modes.");
            if (!NativePawnControlState.IsReady) return Refuse(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var map = ProtoBoundary.LoadedMap(context);
                var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                var effect = new Receipts.CombatOrdersEffect();
                evidence = new Receipts.EffectEvidence { CombatOrders = effect };
                using (authority.Owned())
                {
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Combat orders authority changed before native effect.");
                    // Each pawn's token is checked once, before any order runs:
                    // a pawn's own earlier order in this batch rotates it.
                    var pawns = new Dictionary<string, (Pawn? pawn, string refusal)>();
                    foreach (var order in command.Orders)
                        if (order.Pawn != null && !pawns.ContainsKey(order.Pawn.EntityId)) pawns[order.Pawn.EntityId] = Admit(identity, map, order.Pawn);
                    for (int i = 0; i < command.Orders.Count; i++)
                    {
                        var order = command.Orders[i];
                        var result = new Receipts.CombatOrderResult { Index = (uint)i };
                        string refusal; string? job = null;
                        if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Door) refusal = Door(map, order.Door);
                        else
                        {
                            result.PawnId = order.Pawn.EntityId;
                            var admitted = pawns[order.Pawn.EntityId];
                            refusal = admitted.refusal;
                            if (refusal.Length == 0) refusal = Owned(identity, admitted.pawn!);
                            if (refusal.Length == 0)
                            {
                                try { refusal = Apply(identity, context.Identity, map, admitted.pawn!, order, out job); }
                                catch (Exception error) { refusal = "native_refused"; Log.Warning("[RimGovernor] combat order " + i + " failed: " + error.GetType().Name); }
                            }
                        }
                        result.Applied = refusal.Length == 0;
                        if (refusal.Length > 0) result.Refusal = refusal;
                        if (job != null) result.JobDef = job;
                        effect.Results.Add(result);
                    }
                }
                if (effect.Results.Any(r => r.Applied)) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                return new Operations.ExecuteReply { Receipt = state.Ledger.FinishNoChange(handle, evidence, "Every combat order was refused; see each result's reason.") };
            }
            catch (Exception error)
            {
                return handle == null ? Refuse(Common.FailureCode.NativeFailure, "Combat orders validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted combat orders require observation: " + error.GetType().Name) };
            }
        }

        private static (Pawn? pawn, string refusal) Admit(NativeControlIdentity identity, Map map, Operations.EntityPrecondition entity)
        {
            var pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == entity.EntityId);
            if (pawn == null) return (null, "not_found");
            if (!NativeDraftProtocol.TokenSent(entity)) return (pawn, "");
            var check = NativePawnControlState.Check(identity, pawn, entity.ExpectedSnapshotToken, out _);
            if (check == NativePawnControlResult.StaleSnapshot) return (pawn, "stale_snapshot");
            return check == NativePawnControlResult.Ready ? (pawn, "") : (pawn, "draft_ownership");
        }

        // An eligible drafted pawn under an existing native claim (the owned
        // draft rule of policy/owned_draft.go); rechecked per order.
        private static string Owned(NativeControlIdentity identity, Pawn pawn) =>
            NativePawnControlState.Observe(identity, pawn, out var snapshot) == NativePawnControlResult.Ready && snapshot != null
                && NativeMovementOperations.Owns(snapshot) && pawn.drafter != null ? "" : "draft_ownership";

        private static string Apply(NativeControlIdentity identity, Common.Identity colony, Map map, Pawn pawn, Operations.CombatOrder order, out string? job)
        {
            job = null;
            switch (order.OrderCase)
            {
                case Operations.CombatOrder.OrderOneofCase.Move:
                {
                    var cell = new IntVec3(order.Move.X, 0, order.Move.Z);
                    if (!NativeMovementOperations.Legal(pawn, cell)) return "unreachable";
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.Goto, cell), out job);
                }
                case Operations.CombatOrder.OrderOneofCase.Attack:
                {
                    var player = Faction.OfPlayerSilentFail;
                    Thing? target = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == order.Attack.EntityId)
                        ?? (player == null ? null : NativeObservationTools.HostileBuildings(map, player).SingleOrDefault(t => t.GetUniqueLoadID() == order.Attack.EntityId));
                    if (target == null) return "not_found";
                    if (NativeDraftProtocol.TokenSent(order.Attack))
                    {
                        bool fresh = target is Pawn targetPawn
                            ? NativePawnControlState.Check(identity, targetPawn, order.Attack.ExpectedSnapshotToken, out _) == NativePawnControlResult.Ready
                            : NativeWasteOperations.Token(colony, target) == order.Attack.ExpectedSnapshotToken;
                        if (!fresh) return "stale_snapshot";
                    }
                    var attack = new Operations.AttackTarget { Pawn = order.Pawn, Target = order.Attack, Mode = Operations.AttackMode.Auto };
                    if (!NativeCombatOperations.Legal(attack, pawn, target, out var definition, out var verb) || definition == null) return "cannot_hit";
                    var made = JobMaker.MakeJob(definition, target);
                    made.killIncappedTarget = definition == JobDefOf.AttackMelee && NativeCombatDamageRecord.Downed(target);
                    if (verb != null) NativeCombatOperations.ConfigureRangedJob(made, verb, target);
                    return Take(pawn, made, out job);
                }
                case Operations.CombatOrder.OrderOneofCase.AttackGround:
                {
                    var cell = new IntVec3(order.AttackGround.X, 0, order.AttackGround.Z);
                    var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
                    if (verb == null || verb.verbProps.IsMeleeAttack || !verb.verbProps.targetParams.canTargetLocations || !verb.Available()) return "no_ground_verb";
                    if (pawn.WorkTagIsDisabled(WorkTags.Violent) || !cell.InBounds(map) || !verb.CanHitTarget(cell)) return "cannot_hit";
                    var minimum = verb.verbProps.EffectiveMinRange(cell, pawn);
                    if (pawn.Position.DistanceToSquared(cell) < minimum * minimum && pawn.Position.AdjacentTo8WayOrInside(cell)) return "cannot_hit";
                    // Vanilla Verb.OrderForceTarget's weapon job shape.
                    var made = JobMaker.MakeJob(verb.verbProps.ai_IsWeapon ? JobDefOf.AttackStatic : JobDefOf.UseVerbOnThing);
                    made.verbToUse = verb; made.targetA = cell; made.endIfCantShootInMelee = true;
                    return Take(pawn, made, out job);
                }
                case Operations.CombatOrder.OrderOneofCase.FireMode:
                    pawn.drafter.FireAtWill = order.FireMode == Operations.CombatFireMode.AtWill;
                    return "";
                case Operations.CombatOrder.OrderOneofCase.HoldPosition:
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.Wait_Combat, pawn.Position), out job);
                case Operations.CombatOrder.OrderOneofCase.Stop:
                    pawn.jobs.ClearQueuedJobs();
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    return "";
            }
            return "native_refused";
        }

        private static string Take(Pawn pawn, Job made, out string? job)
        {
            made.playerForced = true;
            job = made.def.defName;
            return pawn.jobs.TryTakeOrderedJob(made, JobTag.Misc) ? "" : "native_refused";
        }

        // The vanilla hold-open toggle on a player door; close clears it so
        // the door shuts once nothing stands in it.
        private static string Door(Map map, Operations.CombatDoor order)
        {
            var cell = new IntVec3(order.Cell.X, 0, order.Cell.Z);
            if (!cell.InBounds(map)) return "not_a_door";
            var door = cell.GetEdifice(map) as Building_Door;
            if (door == null || door.Faction != Faction.OfPlayerSilentFail) return "not_a_door";
            bool open = order.Mode == Operations.CombatDoorMode.HoldOpen;
            if (door.HoldOpen != open) HoldOpenField.SetValue(door, open);
            return "";
        }

        private static readonly System.Reflection.FieldInfo HoldOpenField = typeof(Building_Door).GetField("holdOpenInt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new MissingFieldException("Building_Door.holdOpenInt");

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
