#nullable enable
using RimGovernor.Host.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // CombatOrders: one batch of combat micro orders applied in request
    // order inside one call, so on one game tick. Validation reuses the attack
    // (NativeCombatOperations.Legal), movement (NativeMovementOperations.Legal)
    // and drafted-pawn (NativeMovementOperations.Owns) rules. A draft order
    // drafts the pawn its later orders in the batch need; the fight
    // plan owns that draft. Per-order refusals are
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
                bool door = order.Order is Operations.CombatOrderOrder.DoorCase || order.Order is Operations.CombatOrderOrder.MortarFireCase;
                if (door ? order.Pawn != null : !NativeDraftProtocol.ValidEntityTokenOptional(order.Pawn)) return false;
                var ownId = order.Pawn?.EntityId ?? "";
                switch (order.Order)
                {
                    case Operations.CombatOrderOrder.MoveCase { Value: var move }: if (!ValidCell(move)) return false; break;
                    case Operations.CombatOrderOrder.AttackGroundCase { Value: var ground }: if (!ValidCell(ground)) return false; break;
                    case Operations.CombatOrderOrder.AttackCase { Value: var attack }:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(attack) || attack.EntityId == ownId) return false; break;
                    case Operations.CombatOrderOrder.FireModeCase { Value: var mode }:
                        if (mode != Operations.CombatFireMode.AtWill && mode != Operations.CombatFireMode.Hold) return false; break;
                    case Operations.CombatOrderOrder.DoorCase { Value: var doorOrder }:
                        if (!ValidCell(doorOrder.Cell) || doorOrder.Mode is null
                            || doorOrder.Mode < Operations.CombatDoorMode.HoldOpen || doorOrder.Mode > Operations.CombatDoorMode.Allow) return false; break;
                    case Operations.CombatOrderOrder.RescueCase { Value: var rescue }:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(rescue.Downed) || rescue.Downed?.EntityId == ownId
                            || rescue.Dest != null && !ValidCell(rescue.Dest)) return false; break;
                    case Operations.CombatOrderOrder.RepairCase { Value: var repair }: if (!ValidCell(repair.Cell)) return false; break;
                    case Operations.CombatOrderOrder.ManMortarCase { Value: var manMortar }: if (!ValidCell(manMortar)) return false; break;
                    case Operations.CombatOrderOrder.MortarFireCase { Value: var mortarFire }:
                        if (!ValidCell(mortarFire.Mortar) || mortarFire.Target != null && !ValidCell(mortarFire.Target)
                            || mortarFire.Target == null && mortarFire.Shell is not null) return false; break;
                    case Operations.CombatOrderOrder.ReleaseCase { Value: var release }:
                        if (!NativeDraftProtocol.ValidEntityTokenOptional(release) || release.EntityId == ownId) return false; break;
                    case Operations.CombatOrderOrder.AnimalAreaCase { Value: var animalArea }:
                        if (animalArea.Area is Operations.CombatAnimalAreaArea.CellCase { Value: var areaCell } ? !ValidCell(areaCell)
                            : animalArea.Area is not Operations.CombatAnimalAreaArea.ClearCase) return false; break;
                    case Operations.CombatOrderOrder.CombatDrugCase { Value: var drug }:
                        if (string.IsNullOrEmpty(drug)) return false; break;
                    case Operations.CombatOrderOrder.HoldPositionCase:
                    case Operations.CombatOrderOrder.StopCase:
                    case Operations.CombatOrderOrder.DraftCase: break;
                    default: return false;
                }
            }
            return true;
        }

        private static bool ValidCell(Common.Cell? cell) => cell != null && cell.X is not null && cell.Z is not null && cell.X.GetValueOrDefault() >= 0 && cell.Z.GetValueOrDefault() >= 0;

        // Applies every order and returns the batch effect; per-order
        // refusals are results. The caller holds native authority.
        internal static Receipts.EffectEvidence Apply(Operations.CombatOrders? command, Common.ObservationContext context)
        {
            if (command == null) throw new InvalidOperationException("Combat orders are missing");
            var map = ProtoBoundary.LoadedMap(context);
            var colony = context.Identity ?? new Common.Identity();
            var identity = new NativeControlIdentity(Current.Game, map, (colony.ColonyId ?? ""), (colony.LoadToken ?? ""));
            var effect = new Receipts.CombatOrdersEffect();
            // Each pawn's token is checked once, before any order runs:
            // a pawn's own earlier order in this batch rotates it.
            var pawns = new Dictionary<string, (Pawn? pawn, string refusal)>();
            foreach (var order in command.Orders)
                if (order.Pawn != null && !pawns.ContainsKey((order.Pawn.EntityId ?? ""))) pawns[(order.Pawn.EntityId ?? "")] = Admit(identity, map, order.Pawn);
            for (int i = 0; i < command.Orders.Count; i++)
            {
                var order = command.Orders[i];
                var orderPawn = order.Pawn ?? new Operations.EntityPrecondition();
                var result = new Receipts.CombatOrderResult { Index = (uint)i };
                string refusal; string? job = null;
                if (order.Order is Operations.CombatOrderOrder.DoorCase { Value: var doorOrder }) refusal = Door(map, doorOrder);
                else if (order.Order is Operations.CombatOrderOrder.MortarFireCase { Value: var mortarOrder })
                {
                    try { refusal = MortarFire(map, mortarOrder, out job); }
                    catch (Exception error) { refusal = "native_refused"; ModLog.Warn("combat", "combat order " + i + " mortar_fire failed: " + error.GetType().Name); }
                }
                else if (order.Order is Operations.CombatOrderOrder.ReleaseCase || order.Order is Operations.CombatOrderOrder.AnimalAreaCase)
                {
                    // Animal orders: a player animal, never drafted.
                    result = result with { PawnId = orderPawn.EntityId };
                    var admitted = pawns[(orderPawn.EntityId ?? "")];
                    refusal = admitted.refusal;
                    if (refusal.Length == 0 || refusal == "not_found" && order.Order is Operations.CombatOrderOrder.AnimalAreaCase { Value.Area: Operations.CombatAnimalAreaArea.ClearCase })
                    {
                        try { refusal = Animal(identity, map, admitted.pawn, order, out job); }
                        catch (Exception error) { refusal = "native_refused"; ModLog.Warn("combat", "combat order " + i + " animal order failed: " + error.GetType().Name); }
                    }
                }
                else
                {
                    result = result with { PawnId = orderPawn.EntityId };
                    var admitted = pawns[(orderPawn.EntityId ?? "")];
                    refusal = admitted.refusal;
                    bool draft = order.Order is Operations.CombatOrderOrder.DraftCase;
                    if (refusal.Length == 0) refusal = draft ? Draft(identity, admitted.pawn!) : Drafted(identity, admitted.pawn!);
                    if (refusal.Length == 0 && !draft)
                    {
                        try { refusal = Apply(identity, colony, map, admitted.pawn!, order, out job); }
                        catch (Exception error) { refusal = "native_refused"; ModLog.Warn("combat", "combat order " + i + " failed: " + error.GetType().Name); }
                    }
                }
                result = result with { Applied = refusal.Length == 0 };
                if (refusal.Length > 0) result = result with { Refusal = refusal };
                if (job != null) result = result with { JobDef = job };
                effect = effect with { Results = [.. effect.Results, result] };
            }
            return new Receipts.EffectEvidence { Effect = effect };
        }
        private static (Pawn? pawn, string refusal) Admit(NativeControlIdentity identity, Map map, Operations.EntityPrecondition entity)
        {
            var pawn = map.mapPawns.AllPawnsSpawned.ById(entity.EntityId);
            if (pawn == null) return (null, "not_found");
            if (!NativeDraftProtocol.TokenSent(entity)) return (pawn, "");
            var check = NativePawnControlState.Check(identity, pawn, (entity.ExpectedSnapshotToken ?? ""), out _);
            if (check == NativePawnControlResult.StaleSnapshot) return (pawn, "stale_snapshot");
            return check == NativePawnControlResult.Ready ? (pawn, "") : (pawn, "not_drafted");
        }

        // An eligible drafted pawn; rechecked per order. Drafts are
        // plan-owned: the draft is the plan's, not a native claim's.
        private static string Drafted(NativeControlIdentity identity, Pawn pawn) =>
            NativePawnControlState.Observe(identity, pawn, out var snapshot) == NativePawnControlResult.Ready && snapshot != null
                && NativeMovementOperations.Owns(snapshot) && pawn.drafter != null ? "" : "not_drafted";

        // Draft drafts an eligible pawn; one already drafted applies.
        private static string Draft(NativeControlIdentity identity, Pawn pawn)
        {
            if (NativePawnControlState.Observe(identity, pawn, out var before) != NativePawnControlResult.Ready || before == null || !before.Eligible) return "cannot_draft";
            try { if (!before.Drafted) pawn.drafter.Drafted = true; }
            catch (Exception error) { ModLog.Warn("combat", "combat draft failed: " + error.GetType().Name); return "cannot_draft"; }
            return pawn.drafter.Drafted ? "" : "cannot_draft";
        }

        private static string Apply(NativeControlIdentity identity, Common.Identity colony, Map map, Pawn pawn, Operations.CombatOrder order, out string? job)
        {
            job = null;
            switch (order.Order)
            {
                case Operations.CombatOrderOrder.MoveCase { Value: var moveTo }:
                {
                    var cell = new IntVec3(moveTo.X.GetValueOrDefault(), 0, moveTo.Z.GetValueOrDefault());
                    if (!NativeMovementOperations.Legal(pawn, cell)) return "unreachable";
                    var move = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    if (map.exitMapGrid.IsExitCell(cell)) move.exitMapOnArrival = !pawn.IsColonyMech;
                    return Take(pawn, move, out job);
                }
                case Operations.CombatOrderOrder.AttackCase { Value: var attack }:
                {
                    var player = Faction.OfPlayerSilentFail;
                    Thing? target = map.mapPawns.AllPawnsSpawned.ById(attack.EntityId ?? "")
                        ?? (player == null ? null : NativeObservationTools.HostileBuildingThing(map, player, attack.EntityId ?? ""));
                    if (target == null) return "not_found";
                    if (NativeDraftProtocol.TokenSent(attack))
                    {
                        bool fresh = target is Pawn targetPawn
                            ? NativePawnControlState.Check(identity, targetPawn, attack.ExpectedSnapshotToken ?? "", out _) == NativePawnControlResult.Ready
                            : NativeObservationTools.Token(colony, target) == attack.ExpectedSnapshotToken;
                        if (!fresh) return "stale_snapshot";
                    }
                    if (!NativeCombatOperations.Legal(pawn, target, out var definition, out var verb) || definition == null) return "cannot_hit";
                    var made = JobMaker.MakeJob(definition, target);
                    made.killIncappedTarget = definition == JobDefOf.AttackMelee && NativeCombatOperations.Downed(target);
                    if (verb != null) NativeCombatOperations.ConfigureRangedJob(made, verb, target);
                    return Take(pawn, made, out job);
                }
                case Operations.CombatOrderOrder.AttackGroundCase { Value: var ground }:
                {
                    var cell = new IntVec3(ground.X.GetValueOrDefault(), 0, ground.Z.GetValueOrDefault());
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
                case Operations.CombatOrderOrder.FireModeCase { Value: var mode }:
                    pawn.drafter.FireAtWill = mode == Operations.CombatFireMode.AtWill;
                    return "";
                case Operations.CombatOrderOrder.HoldPositionCase:
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.Wait_Combat, pawn.Position), out job);
                case Operations.CombatOrderOrder.RescueCase { Value: var rescue }:
                {
                    // The pawn-target rescue order's rules (NativeCustodyOperations).
                    var downed = rescue.Downed ?? new Operations.EntityPrecondition();
                    var patient = map.mapPawns.AllPawnsSpawned.ById(downed.EntityId ?? "");
                    if (patient == null) return "not_found";
                    if (NativeDraftProtocol.TokenSent(downed)
                        && NativePawnControlState.Check(identity, patient, downed.ExpectedSnapshotToken ?? "", out _) != NativePawnControlResult.Ready) return "stale_snapshot";
                    if (!NativeCustodyOperations.RescueEligible(pawn, patient)) return "cannot_rescue";
                    Building_Bed? bed;
                    if (rescue.Dest != null)
                    {
                        var dest = new IntVec3(rescue.Dest.X.GetValueOrDefault(), 0, rescue.Dest.Z.GetValueOrDefault());
                        bed = dest.InBounds(map) ? dest.GetFirstBuilding(map) as Building_Bed : null;
                        if (bed == null || !RestUtility.IsValidBedFor(bed, patient, pawn, checkSocialProperness: false)) return "no_bed";
                    }
                    else if (!NativeCustodyOperations.FindBed(JobOrderKind.Rescue, pawn, patient, out bed) || bed == null) return "no_bed";
                    if (!pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                    var made = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                    made.count = 1;
                    return Take(pawn, made, out job);
                }
                case Operations.CombatOrderOrder.RepairCase { Value: var repair }:
                {
                    // The vanilla Repair job on a damaged player building
                    //; the pawn stays drafted under the fight.
                    var cell = new IntVec3(repair.Cell?.X.GetValueOrDefault() ?? 0, 0, repair.Cell?.Z.GetValueOrDefault() ?? 0);
                    var building = cell.InBounds(map) ? cell.GetEdifice(map) : null;
                    if (building == null || building.Faction != Faction.OfPlayerSilentFail || !building.def.useHitPoints
                        || building.HitPoints >= building.MaxHitPoints || building.IsBurning()
                        || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return "cannot_repair";
                    if (!pawn.CanReserveAndReach(building, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.Repair, building), out job);
                }
                case Operations.CombatOrderOrder.ManMortarCase { Value: var manMortar }:
                {
                    // Crew the vanilla mortar: ManTurret, which loads
                    // shells by the mortar's filter; the aim is mortar_fire's.
                    var mortar = Mortar(map, manMortar);
                    if (mortar == null) return "not_a_mortar";
                    if (mortar.GetComp<CompMannable>().ManningPawn == pawn) { job = JobDefOf.ManTurret.defName; return ""; }
                    if (!pawn.CanReserveAndReach(mortar, PathEndMode.InteractionCell, Danger.Deadly)) return "unreachable";
                    return Take(pawn, JobMaker.MakeJob(JobDefOf.ManTurret, mortar), out job);
                }
                case Operations.CombatOrderOrder.CombatDrugCase { Value: var combatDrug }:
                    return CombatDrug(pawn, combatDrug, out job);
                case Operations.CombatOrderOrder.StopCase:
                    pawn.jobs.ClearQueuedJobs();
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    return "";
            }
            return "native_refused";
        }
        // Animal orders on one spawned player-faction animal.
        // Release: the vanilla released animal's melee attack on one pawn.
        // AnimalArea: a one-cell native allowed area, labelled by the animal
        // so it survives a reload and clear still finds it; clear restores
        // the restriction the animal had before, none when unknown.
        private static string Animal(NativeControlIdentity identity, Map map, Pawn? animal, Operations.CombatOrder order, out string? job)
        {
            job = null;
            var player = Faction.OfPlayerSilentFail;
            if (player == null) return "native_refused";
            var orderPawnId = order.Pawn?.EntityId ?? "";
            var areaOrder = order.Order is Operations.CombatOrderOrder.AnimalAreaCase { Value: var areaValue } ? areaValue : null;
            bool clear = areaOrder?.Area is Operations.CombatAnimalAreaArea.ClearCase;
            if (animal == null || animal.Faction != player || animal.RaceProps?.Animal != true || !animal.Spawned || animal.Dead)
            {
                // Loaded-map lookup and live ownership are authoritative. Delete
                // only this command's temporary area before certifying impossibility.
                if (clear)
                {
                    (map.areaManager.GetLabeled(CombatAreaPrefix + orderPawnId) as Area_Allowed)?.Delete();
                    PriorAreas.Remove(orderPawnId);
                }
                return animal == null ? "not_found" : "not_ours";
            }
            if (order.Order is Operations.CombatOrderOrder.ReleaseCase { Value: var release })
            {
                if (animal.training == null || !animal.training.HasLearned(TrainableDefOf.Release)) return "untrained";
                var target = map.mapPawns.AllPawnsSpawned.ById(release.EntityId ?? "");
                if (target == null) return "not_found";
                if (NativeDraftProtocol.TokenSent(release)
                    && NativePawnControlState.Check(identity, target, release.ExpectedSnapshotToken ?? "", out _) != NativePawnControlResult.Ready) return "stale_snapshot";
                if (target.Dead || target.Downed || animal.Downed || animal.InMentalState || target.Faction == player) return "cannot_hit";
                if (!animal.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return "unreachable";
                return Take(animal, JobMaker.MakeJob(JobDefOf.AttackMelee, target), out job);
            }
            var id = animal.GetUniqueLoadID();
            var owned = map.areaManager.GetLabeled(CombatAreaPrefix + id) as Area_Allowed;
            if (clear)
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
            var areaCell = areaOrder?.Area?.Cell;
            var cell = new IntVec3(areaCell?.X.GetValueOrDefault() ?? 0, 0, areaCell?.Z.GetValueOrDefault() ?? 0);
            if (!cell.InBounds(map) || !cell.Standable(map) || !animal.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return "unreachable";
            if (owned == null)
            {
                if (!map.areaManager.CanMakeNewAllowed()) return "native_refused";
                owned = new Area_Allowed(map.areaManager, CombatAreaPrefix + id);
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

        // The label of the allowed area an animal_area order owns.
        internal const string CombatAreaPrefix = "Combat ";

        // Combat drug: the carried drug Go chose by def name, by the
        // vanilla Ingest job from inventory; the def must be a drug.
        // Go owns who may dose; vanilla's refusal is native_refused.
        private static string CombatDrug(Pawn pawn, string defName, out string? job)
        {
            job = null;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def?.GetCompProperties<CompProperties_Drug>() == null) return "not_a_drug";
            var drug = pawn.inventory?.innerContainer.FirstOrDefault(t => t.def == def);
            if (drug == null) return "no_drug";
            var made = JobMaker.MakeJob(JobDefOf.Ingest, drug);
            made.count = 1;
            return Take(pawn, made, out job);
        }

        private static string Take(Pawn pawn, Job made, out string? job)
        {
            made.playerForced = true;
            job = made.def.defName;
            return pawn.jobs.TryTakeOrderedJob(made, JobTag.Misc) ? "" : "native_refused";
        }

        // The unroofed player mannable mortar on cell, else null.
        private static Building_TurretGun? Mortar(Map map, Common.Cell? at)
        {
            if (at == null) return null;
            var cell = new IntVec3(at.X.GetValueOrDefault(), 0, at.Z.GetValueOrDefault());
            var mortar = cell.InBounds(map) ? cell.GetEdifice(map) as Building_TurretGun : null;
            return mortar == null || mortar.Faction != Faction.OfPlayerSilentFail || mortar.def.building?.IsMortar != true
                || mortar.GetComp<CompMannable>() == null || map.roofGrid.Roofed(mortar.Position) ? null : mortar;
        }

        // MortarFire (aim and shell): the attack
        // gizmo's forced target, no pawn. A different loaded shell is
        // unloaded beside the mortar and the filter allows only the asked
        // one; a pawn manning it retakes ManTurret, whose ammo search
        // honours the filter.
        private static string MortarFire(Map map, Operations.CombatMortarFire order, out string? job)
        {
            job = null;
            var mortar = Mortar(map, order.Mortar);
            if (mortar == null) return "not_a_mortar";
            if (order.Target == null)
            {
                // No target: the gizmo's stop-forced-attack, the
                // aim gone when the siege breaks camp. An invalid target
                // resets the forced target (vanilla OrderAttack).
                mortar.OrderAttack(LocalTargetInfo.Invalid);
                return "";
            }
            var target = new IntVec3(order.Target.X.GetValueOrDefault(), 0, order.Target.Z.GetValueOrDefault());
            var verb = mortar.AttackVerb;
            var distance = (target - mortar.Position).LengthHorizontal;
            if (!target.InBounds(map) || verb == null || distance < verb.verbProps.EffectiveMinRange(target, mortar) || distance > verb.EffectiveRange) return "cannot_hit";
            var crew = mortar.GetComp<CompMannable>().ManningPawn;
            if (order.Shell is not null && (order.Shell ?? "").Length > 0)
            {
                var shells = mortar.gun?.TryGetComp<CompChangeableProjectile>();
                var shell = DefDatabase<ThingDef>.GetNamedSilentFail((order.Shell ?? ""));
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
            var cell = new IntVec3(order.Cell?.X.GetValueOrDefault() ?? 0, 0, order.Cell?.Z.GetValueOrDefault() ?? 0);
            if (!cell.InBounds(map)) return "not_a_door";
            var door = cell.GetEdifice(map) as Building_Door;
            if (door == null || door.Faction != Faction.OfPlayerSilentFail) return "not_a_door";
            if (order.Mode.GetValueOrDefault() == Operations.CombatDoorMode.Forbid || order.Mode.GetValueOrDefault() == Operations.CombatDoorMode.Allow)
            {
                // The vanilla forbid toggle: forbidden doors bar every
                // player pawn's path, drafted or not.
                door.SetForbidden(order.Mode.GetValueOrDefault() == Operations.CombatDoorMode.Forbid, warnOnFail: false);
                return "";
            }
            bool open = order.Mode.GetValueOrDefault() == Operations.CombatDoorMode.HoldOpen;
            if (door.HoldOpen != open) HoldOpenField.SetValue(door, open);
            return "";
        }

        private static readonly System.Reflection.FieldInfo HoldOpenField = typeof(Building_Door).GetField("holdOpenInt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new MissingFieldException("Building_Door.holdOpenInt");

    }

    // The CombatOrders arm of Actions/Apply. The batch applies when
    // it is well formed; each order's refusal is a result in the effect.
    internal sealed class CombatOrdersActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            if (!NativeCombatOrders.Valid(action.Intent?.CombatOrders))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Combat orders need at least one order, each an exact pawn (none for a door or mortar_fire) and one supported order with explicit cells and modes.");
            return NativePawnControlState.IsReady ? null : ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
        }
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeCombatOrders.Apply(action.Intent?.CombatOrders, context);
    }

    // The Draft arm of Actions/Apply: draft or undraft one eligible
    // colonist. The wanted state already holding applies without a change.
    internal sealed class DraftActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Intent?.Draft, context, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Intent?.Draft;
            var failure = Resolve(intent, context, out var pawn);
            if (failure != null) throw new InvalidOperationException("Draft prerequisites changed before apply: " + (failure.Detail ?? ""));
            if (intent == null || pawn == null) throw new InvalidOperationException("Draft prerequisites changed before apply.");
            bool wanted = intent.Drafted.GetValueOrDefault();
            bool issued = pawn.drafter.Drafted != wanted;
            if (issued) pawn.drafter.Drafted = wanted;
            if (pawn.drafter.Drafted != wanted) throw new InvalidOperationException("Native draft state did not change.");
            return new Receipts.EffectEvidence { Effect = new Receipts.JobEffect { PawnId = pawn.GetUniqueLoadID(), Issued = issued, Verified = true, Drafted = pawn.Drafted,
                VerifiedReason = issued ? "Native draft state set." : "The draft state already matched." } };
        }

        private static Common.Failure? Resolve(Operations.DraftIntent? intent, Common.ObservationContext context, out Pawn? pawn)
        {
            pawn = null;
            if (intent == null || intent.PawnId is null || intent.Drafted is null || !ProtoBoundary.IsIdentifier(intent.PawnId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A draft requires a pawn id and the wanted draft state.");
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (pawn == null || pawn.Dead) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The pawn is not alive and spawned on this map.");
            var identity = new NativeControlIdentity(Current.Game, map, (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
            var check = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            if (check != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(check, context);
            if (snapshot.Drafted == intent.Drafted.GetValueOrDefault()) return null;
            return snapshot.Eligible ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "cannot_draft: the pawn is not an eligible player colonist.");
        }
    }
}
