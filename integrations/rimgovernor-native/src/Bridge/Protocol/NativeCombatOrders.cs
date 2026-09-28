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
    // and owned-draft (NativeMovementOperations.Owns) rules. A draft
    // order (#910) makes the claim the pawn's later orders in the batch need,
    // by the SetDrafted claim or adoption rules. Per-order refusals are
    // results, not call failures. The receipt certifies the orders were
    // taken; observing the fight is the caller's next read.
    internal static class NativeCombatOrders
    {
        internal static bool Valid(Operations.CombatOrders? command)
        {
            if (command == null || command.Orders.Count < 1) return false;
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
                            || order.Door.Mode < Operations.CombatDoorMode.HoldOpen || order.Door.Mode > Operations.CombatDoorMode.Allow) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Rescue:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(order.Rescue.Downed) || order.Rescue.Downed.EntityId == order.Pawn!.EntityId
                            || order.Rescue.Dest != null && !ValidCell(order.Rescue.Dest)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Repair: if (!ValidCell(order.Repair.Cell)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Mortar: if (!ValidCell(order.Mortar.Mortar) || !ValidCell(order.Mortar.Target)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.Release:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(order.Release) || order.Release.EntityId == order.Pawn!.EntityId) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.AnimalArea:
                        if (order.AnimalArea.AreaCase == Operations.CombatAnimalArea.AreaOneofCase.Cell ? !ValidCell(order.AnimalArea.Cell)
                            : order.AnimalArea.AreaCase != Operations.CombatAnimalArea.AreaOneofCase.Clear) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.HoldPosition:
                    case Operations.CombatOrder.OrderOneofCase.Stop:
                    case Operations.CombatOrder.OrderOneofCase.Draft: break;
                    default: return false;
                }
            }
            return true;
        }

        private static bool ValidCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0;

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.CombatOrders; var pre = request.Precondition;
            if (!Valid(command)) return Refuse(Common.FailureCode.InvalidRequest, "Combat orders need at least one order, each an exact pawn (none for a door) and one supported order with explicit cells and modes.");
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
                        string refusal; string? job = null; string? claim = null;
                        if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Door) refusal = Door(map, order.Door);
                        else if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Draft)
                        {
                            result.PawnId = order.Pawn.EntityId;
                            var admitted = pawns[order.Pawn.EntityId];
                            refusal = admitted.refusal;
                            if (refusal.Length == 0)
                            {
                                try { refusal = Draft(identity, admitted.pawn!, out claim); }
                                catch (Exception error) { refusal = "cannot_draft"; Log.Warning("[RimGovernor] combat order " + i + " draft failed: " + error.GetType().Name); }
                            }
                        }
                        else if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Release || order.OrderCase == Operations.CombatOrder.OrderOneofCase.AnimalArea)
                        {
                            // Animal orders (#1057): a player animal, no draft claim.
                            result.PawnId = order.Pawn.EntityId;
                            var admitted = pawns[order.Pawn.EntityId];
                            refusal = admitted.refusal;
                            if (refusal.Length == 0)
                            {
                                try { refusal = Animal(identity, map, admitted.pawn!, order, out job); }
                                catch (Exception error) { refusal = "native_refused"; Log.Warning("[RimGovernor] combat order " + i + " animal order failed: " + error.GetType().Name); }
                            }
                        }
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
                        if (claim != null && refusal.Length == 0) result.DraftClaimId = claim;
                        effect.Results.Add(result);
                    }
                }
                if (effect.Results.Any(r => r.Applied)) return new Operations.ExecuteReply { Receipt = state.Ledger.FinishApplied(handle, evidence) };
                return new Operations.ExecuteReply { Receipt = state.Ledger.FinishNoChange(handle, evidence, "Every combat order was refused; see each result's reason.") };
            }
            catch (Exception error)
            {
                return handle == null ? Refuse(Common.FailureCode.NativeFailure, "Combat orders validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = state.Ledger.FinishUncertain(handle, evidence, "Admitted combat orders require observation: " + error.GetType().Name) };
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

        // Draft (#910) claims a pawn nobody claims, under the owned scope the
        // batch runs in: an undrafted eligible pawn by the SetDrafted claim
        // (setter between prepare and complete), a drafted unclaimed one by
        // adoption (no setter). A pawn another claim holds is refused.
        private static string Draft(NativeControlIdentity identity, Pawn pawn, out string? claim)
        {
            claim = null;
            if (NativePawnControlState.Observe(identity, pawn, out var before) != NativePawnControlResult.Ready || before == null) return "cannot_draft";
            if (before.Claim != null) return "draft_ownership";
            if (!before.Eligible) return "cannot_draft";
            NativePawnSnapshot? after;
            if (before.Drafted)
            {
                if (NativePawnControlState.PrepareAdopt(identity, pawn, before.Token, out var adopt, out _) != NativePawnControlResult.Ready) return "cannot_draft";
                if (NativePawnControlState.CompleteAdopt(adopt!, out after) != NativePawnControlResult.Ready) return "cannot_draft";
            }
            else
            {
                if (NativePawnControlState.PrepareClaim(identity, pawn, before.Token, out var ticket, out _) != NativePawnControlResult.Ready) return "cannot_draft";
                pawn.drafter.Drafted = true;
                if (NativePawnControlState.CompleteClaim(ticket!, out after) != NativePawnControlResult.Ready) return "cannot_draft";
            }
            if (after == null || !after.Drafted || after.Claim == null) return "cannot_draft";
            claim = after.Claim.ClaimId;
            return "";
        }

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
                        ?? (player == null ? null : NativeObservationTools.HostileBuildingThing(map, player, order.Attack.EntityId));
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
                case Operations.CombatOrder.OrderOneofCase.Rescue:
                {
                    // The pawn-target rescue order's rules (NativeCustodyOperations).
                    var patient = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == order.Rescue.Downed.EntityId);
                    if (patient == null) return "not_found";
                    if (NativeDraftProtocol.TokenSent(order.Rescue.Downed)
                        && NativePawnControlState.Check(identity, patient, order.Rescue.Downed.ExpectedSnapshotToken, out _) != NativePawnControlResult.Ready) return "stale_snapshot";
                    if (!NativeCustodyOperations.RescueEligible(pawn, patient)) return "cannot_rescue";
                    Building_Bed? bed;
                    if (order.Rescue.Dest != null)
                    {
                        var dest = new IntVec3(order.Rescue.Dest.X, 0, order.Rescue.Dest.Z);
                        bed = dest.InBounds(map) ? dest.GetFirstBuilding(map) as Building_Bed : null;
                        if (bed == null || !RestUtility.IsValidBedFor(bed, patient, pawn, checkSocialProperness: false)) return "no_bed";
                    }
                    else if (!NativeCustodyOperations.FindBed(Operations.PawnOrderKind.Rescue, pawn, patient, out bed) || bed == null) return "no_bed";
                    if (!pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                    var made = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                    made.count = 1;
                    return Take(pawn, made, out job);
                }
                case Operations.CombatOrder.OrderOneofCase.Repair:
                {
                    // The vanilla Repair job on a damaged player building
                    // (#900); the pawn stays drafted under the fight.
                    var cell = new IntVec3(order.Repair.Cell.X, 0, order.Repair.Cell.Z);
                    var building = cell.InBounds(map) ? cell.GetEdifice(map) : null;
                    if (building == null || building.Faction != Faction.OfPlayerSilentFail || !building.def.useHitPoints
                        || building.HitPoints >= building.MaxHitPoints || building.IsBurning()
                        || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return "cannot_repair";
                    if (!pawn.CanReserveAndReach(building, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.Repair, building), out job);
                }
                case Operations.CombatOrder.OrderOneofCase.Mortar:
                {
                    // The vanilla mortar (#931): a manning pawn (ManTurret,
                    // which loads shells) and the attack gizmo's forced target.
                    var cell = new IntVec3(order.Mortar.Mortar.X, 0, order.Mortar.Mortar.Z);
                    var target = new IntVec3(order.Mortar.Target.X, 0, order.Mortar.Target.Z);
                    var mortar = cell.InBounds(map) ? cell.GetEdifice(map) as Building_TurretGun : null;
                    if (mortar == null || mortar.Faction != Faction.OfPlayerSilentFail || mortar.def.building?.IsMortar != true
                        || mortar.GetComp<CompMannable>() == null || map.roofGrid.Roofed(mortar.Position)) return "not_a_mortar";
                    var verb = mortar.AttackVerb;
                    var distance = (target - mortar.Position).LengthHorizontal;
                    if (!target.InBounds(map) || verb == null || distance < verb.verbProps.EffectiveMinRange(target, mortar) || distance > verb.EffectiveRange) return "cannot_hit";
                    if (!pawn.CanReserveAndReach(mortar, PathEndMode.InteractionCell, Danger.Deadly)) return "unreachable";
                    var reload = false;
                    if (order.Mortar.HasShell && order.Mortar.Shell.Length > 0)
                    {
                        // The requested shell (#1051): unload a different one
                        // beside the mortar, allow only this one, and (re)take
                        // ManTurret, whose ammo search honours the filter.
                        var shells = mortar.gun?.TryGetComp<CompChangeableProjectile>();
                        var shell = DefDatabase<ThingDef>.GetNamedSilentFail(order.Mortar.Shell);
                        if (shells == null || shell == null || shells.GetParentStoreSettings()?.AllowedToAccept(shell) != true) return "unknown_shell";
                        if (shells.LoadedShell != shell)
                        {
                            if (!map.listerThings.ThingsOfDef(shell).Any(t => t.Spawned && !t.IsForbidden(pawn) && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Deadly))) return "no_shell";
                            if (shells.Loaded) GenPlace.TryPlaceThing(shells.RemoveShell(), mortar.InteractionCell, map, ThingPlaceMode.Near);
                            reload = true;
                        }
                        shells.allowedShellsSettings.filter.SetDisallowAll();
                        shells.allowedShellsSettings.filter.SetAllow(shell, true);
                    }
                    var refusal = mortar.GetComp<CompMannable>().ManningPawn == pawn && !reload ? "" : Take(pawn, JobMaker.MakeJob(JobDefOf.ManTurret, mortar), out job);
                    if (refusal.Length == 0) { mortar.OrderAttack(target); job ??= JobDefOf.ManTurret.defName; }
                    return refusal;
                }
                case Operations.CombatOrder.OrderOneofCase.Stop:
                    pawn.jobs.ClearQueuedJobs();
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    return "";
            }
            return "native_refused";
        }

        // Animal orders (#1057) on one spawned player-faction animal.
        // Release: the vanilla released animal's melee attack on one pawn.
        // AnimalArea: a one-cell native allowed area, labelled by the animal
        // so it survives a reload and clear still finds it; clear restores
        // the restriction the animal had before, none when unknown.
        private static string Animal(NativeControlIdentity identity, Map map, Pawn animal, Operations.CombatOrder order, out string? job)
        {
            job = null;
            var player = Faction.OfPlayerSilentFail;
            if (player == null || animal.Faction != player || animal.RaceProps?.Animal != true || !animal.Spawned || animal.Dead) return "not_ours";
            if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Release)
            {
                if (animal.training == null || !animal.training.HasLearned(TrainableDefOf.Release)) return "untrained";
                var target = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == order.Release.EntityId);
                if (target == null) return "not_found";
                if (NativeDraftProtocol.TokenSent(order.Release)
                    && NativePawnControlState.Check(identity, target, order.Release.ExpectedSnapshotToken, out _) != NativePawnControlResult.Ready) return "stale_snapshot";
                if (target.Dead || target.Downed || animal.Downed || animal.InMentalState || target.Faction == player) return "cannot_hit";
                if (!animal.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                return Take(animal, JobMaker.MakeJob(JobDefOf.AttackMelee, target), out job);
            }
            var id = animal.GetUniqueLoadID();
            var owned = map.areaManager.GetLabeled("RimGovernor " + id) as Area_Allowed;
            if (order.AnimalArea.AreaCase == Operations.CombatAnimalArea.AreaOneofCase.Clear)
            {
                Area? prior = null;
                if (PriorAreas.TryGetValue(id, out var kept) && kept != null && map.areaManager.AllAreas.Contains(kept)) prior = kept;
                PriorAreas.Remove(id);
                if (animal.playerSettings != null && (owned == null || animal.playerSettings.AreaRestrictionInPawnCurrentMap == owned))
                    animal.playerSettings.AreaRestrictionInPawnCurrentMap = prior;
                owned?.Delete();
                return "";
            }
            if (animal.playerSettings == null || !animal.playerSettings.SupportsAllowedAreas) return "not_ours";
            var cell = new IntVec3(order.AnimalArea.Cell.X, 0, order.AnimalArea.Cell.Z);
            if (!cell.InBounds(map) || !cell.Standable(map) || !animal.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return "unreachable";
            if (owned == null)
            {
                if (!map.areaManager.CanMakeNewAllowed()) return "native_refused";
                owned = new Area_Allowed(map.areaManager, "RimGovernor " + id);
                map.areaManager.AllAreas.Add(owned);
            }
            var current = animal.playerSettings.AreaRestrictionInPawnCurrentMap;
            if (current != owned) PriorAreas[id] = current;
            foreach (var c in owned.ActiveCells.ToList()) owned[c] = false;
            owned[cell] = true;
            animal.playerSettings.AreaRestrictionInPawnCurrentMap = owned;
            return "";
        }

        // The restriction each animal had before its first animal_area order;
        // lost on a reload, when clear falls back to unrestricted.
        private static readonly Dictionary<string, Area?> PriorAreas = new Dictionary<string, Area?>();

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
            if (order.Mode == Operations.CombatDoorMode.Forbid || order.Mode == Operations.CombatDoorMode.Allow)
            {
                // The vanilla forbid toggle (#867): forbidden doors bar every
                // player pawn's path, drafted or not.
                door.SetForbidden(order.Mode == Operations.CombatDoorMode.Forbid, warnOnFail: false);
                return "";
            }
            bool open = order.Mode == Operations.CombatDoorMode.HoldOpen;
            if (door.HoldOpen != open) HoldOpenField.SetValue(door, open);
            return "";
        }

        private static readonly System.Reflection.FieldInfo HoldOpenField = typeof(Building_Door).GetField("holdOpenInt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new MissingFieldException("Building_Door.holdOpenInt");

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
