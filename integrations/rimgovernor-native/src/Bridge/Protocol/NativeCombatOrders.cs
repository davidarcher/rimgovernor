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
    // and drafted-pawn (NativeMovementOperations.Owns) rules. A draft order
    // (#910) drafts the pawn its later orders in the batch need; the fight
    // plan owns that draft (#939). Per-order refusals are
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
                bool door = order.OrderCase == Operations.CombatOrder.OrderOneofCase.Door || order.OrderCase == Operations.CombatOrder.OrderOneofCase.MortarFire;
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
                    case Operations.CombatOrder.OrderOneofCase.ManMortar: if (!ValidCell(order.ManMortar)) return false; break;
                    case Operations.CombatOrder.OrderOneofCase.MortarFire: if (!ValidCell(order.MortarFire.Mortar) || !ValidCell(order.MortarFire.Target)) return false; break;
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

        // Applies every order and returns the batch effect; per-order
        // refusals are results. The caller holds native authority.
        internal static Receipts.EffectEvidence Apply(Operations.CombatOrders command, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            var effect = new Receipts.CombatOrdersEffect();
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
                else if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.MortarFire)
                {
                    try { refusal = MortarFire(map, order.MortarFire, out job); }
                    catch (Exception error) { refusal = "native_refused"; Log.Warning("[RimGovernor] combat order " + i + " mortar_fire failed: " + error.GetType().Name); }
                }
                else if (order.OrderCase == Operations.CombatOrder.OrderOneofCase.Release || order.OrderCase == Operations.CombatOrder.OrderOneofCase.AnimalArea)
                {
                    // Animal orders (#1057): a player animal, never drafted.
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
                    bool draft = order.OrderCase == Operations.CombatOrder.OrderOneofCase.Draft;
                    if (refusal.Length == 0) refusal = draft ? Draft(identity, admitted.pawn!) : Drafted(identity, admitted.pawn!);
                    if (refusal.Length == 0 && !draft)
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
            return new Receipts.EffectEvidence { CombatOrders = effect };
        }

        private static (Pawn? pawn, string refusal) Admit(NativeControlIdentity identity, Map map, Operations.EntityPrecondition entity)
        {
            var pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == entity.EntityId);
            if (pawn == null) return (null, "not_found");
            if (!NativeDraftProtocol.TokenSent(entity)) return (pawn, "");
            var check = NativePawnControlState.Check(identity, pawn, entity.ExpectedSnapshotToken, out _);
            if (check == NativePawnControlResult.StaleSnapshot) return (pawn, "stale_snapshot");
            return check == NativePawnControlResult.Ready ? (pawn, "") : (pawn, "not_drafted");
        }

        // An eligible drafted pawn; rechecked per order. Drafts are
        // plan-owned (#939): the draft is the plan's, not a native claim's.
        private static string Drafted(NativeControlIdentity identity, Pawn pawn) =>
            NativePawnControlState.Observe(identity, pawn, out var snapshot) == NativePawnControlResult.Ready && snapshot != null
                && NativeMovementOperations.Owns(snapshot) && pawn.drafter != null ? "" : "not_drafted";

        // Draft (#910) drafts an eligible pawn; one already drafted applies.
        private static string Draft(NativeControlIdentity identity, Pawn pawn)
        {
            if (NativePawnControlState.Observe(identity, pawn, out var before) != NativePawnControlResult.Ready || before == null || !before.Eligible) return "cannot_draft";
            try { if (!before.Drafted) pawn.drafter.Drafted = true; }
            catch (Exception error) { Log.Warning("[RimGovernor] combat draft failed: " + error.GetType().Name); return "cannot_draft"; }
            return pawn.drafter.Drafted ? "" : "cannot_draft";
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
                    if (!NativeCombatOperations.Legal(pawn, target, out var definition, out var verb) || definition == null) return "cannot_hit";
                    var made = JobMaker.MakeJob(definition, target);
                    made.killIncappedTarget = definition == JobDefOf.AttackMelee && NativeCombatOperations.Downed(target);
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
                case Operations.CombatOrder.OrderOneofCase.ManMortar:
                {
                    // Crew the vanilla mortar (#1202): ManTurret, which loads
                    // shells by the mortar's filter; the aim is mortar_fire's.
                    var mortar = Mortar(map, order.ManMortar);
                    if (mortar == null) return "not_a_mortar";
                    if (mortar.GetComp<CompMannable>().ManningPawn == pawn) { job = JobDefOf.ManTurret.defName; return ""; }
                    if (!pawn.CanReserveAndReach(mortar, PathEndMode.InteractionCell, Danger.Deadly)) return "unreachable";
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.ManTurret, mortar), out job);
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

        // The unroofed player mannable mortar on cell, else null.
        private static Building_TurretGun? Mortar(Map map, Common.Cell at)
        {
            var cell = new IntVec3(at.X, 0, at.Z);
            var mortar = cell.InBounds(map) ? cell.GetEdifice(map) as Building_TurretGun : null;
            return mortar == null || mortar.Faction != Faction.OfPlayerSilentFail || mortar.def.building?.IsMortar != true
                || mortar.GetComp<CompMannable>() == null || map.roofGrid.Roofed(mortar.Position) ? null : mortar;
        }

        // MortarFire (#1202, the #931 aim and #1051 shell): the attack
        // gizmo's forced target, no pawn. A different loaded shell is
        // unloaded beside the mortar and the filter allows only the asked
        // one; a pawn manning it retakes ManTurret, whose ammo search
        // honours the filter.
        private static string MortarFire(Map map, Operations.CombatMortarFire order, out string? job)
        {
            job = null;
            var mortar = Mortar(map, order.Mortar);
            if (mortar == null) return "not_a_mortar";
            var target = new IntVec3(order.Target.X, 0, order.Target.Z);
            var verb = mortar.AttackVerb;
            var distance = (target - mortar.Position).LengthHorizontal;
            if (!target.InBounds(map) || verb == null || distance < verb.verbProps.EffectiveMinRange(target, mortar) || distance > verb.EffectiveRange) return "cannot_hit";
            var crew = mortar.GetComp<CompMannable>().ManningPawn;
            if (order.HasShell && order.Shell.Length > 0)
            {
                var shells = mortar.gun?.TryGetComp<CompChangeableProjectile>();
                var shell = DefDatabase<ThingDef>.GetNamedSilentFail(order.Shell);
                if (shells == null || shell == null || shells.GetParentStoreSettings()?.AllowedToAccept(shell) != true) return "unknown_shell";
                if (shells.LoadedShell != shell)
                {
                    var player = Faction.OfPlayerSilentFail;
                    if (!map.listerThings.ThingsOfDef(shell).Any(t => t.Spawned && (crew != null
                        ? !t.IsForbidden(crew) && crew.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Deadly)
                        : player != null && !t.IsForbidden(player)))) return "no_shell";
                }
                shells.allowedShellsSettings.filter.SetDisallowAll();
                shells.allowedShellsSettings.filter.SetAllow(shell, true);
                if (shells.LoadedShell != shell)
                {
                    if (shells.Loaded) GenPlace.TryPlaceThing(shells.RemoveShell(), mortar.InteractionCell, map, ThingPlaceMode.Near);
                    if (crew != null)
                    {
                        var refusal = Take(crew, JobMaker.MakeJob(JobDefOf.ManTurret, mortar), out job);
                        if (refusal.Length > 0) return refusal;
                    }
                }
            }
            mortar.OrderAttack(target);
            return "";
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

    }

    // The CombatOrders arm of Actions/Apply (#939). The batch applies when
    // it is well formed; each order's refusal is a result in the effect.
    internal sealed class CombatOrdersActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            if (!NativeCombatOrders.Valid(action.CombatOrders))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Combat orders need at least one order, each an exact pawn (none for a door or mortar_fire) and one supported order with explicit cells and modes.");
            return NativePawnControlState.IsReady ? null : ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
        }
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeCombatOrders.Apply(action.CombatOrders, context);
    }

    // The Draft arm of Actions/Apply (#939): draft or undraft one eligible
    // colonist. The wanted state already holding applies without a change.
    internal sealed class DraftActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Draft, context, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Draft, context, out var pawn);
            if (failure != null) throw new InvalidOperationException("Draft prerequisites changed before apply: " + failure.Detail);
            bool issued = pawn!.drafter.Drafted != action.Draft.Drafted;
            if (issued) pawn.drafter.Drafted = action.Draft.Drafted;
            if (pawn.drafter.Drafted != action.Draft.Drafted) throw new InvalidOperationException("Native draft state did not change.");
            return new Receipts.EffectEvidence { Job = new Receipts.JobEffect { PawnId = pawn.GetUniqueLoadID(), Issued = issued, Verified = true, Drafted = pawn.Drafted,
                VerifiedReason = issued ? "Native draft state set." : "The draft state already matched." } };
        }

        private static Common.Failure? Resolve(Operations.DraftIntent? intent, Common.ObservationContext context, out Pawn? pawn)
        {
            pawn = null;
            if (intent == null || !intent.HasPawnId || !intent.HasDrafted || !ProtoBoundary.IsIdentifier(intent.PawnId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A draft requires a pawn id and the wanted draft state.");
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == intent.PawnId);
            if (pawn == null || pawn.Dead) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The pawn is not alive and spawned on this map.");
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            var check = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            if (check != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(check, context);
            if (snapshot.Drafted == intent.Drafted) return null;
            return snapshot.Eligible ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "cannot_draft: the pawn is not an eligible player colonist.");
        }
    }
}
