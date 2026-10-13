#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // IgniteIntent on Actions/Apply: one drafted colonist
    // force-fires the molotov in the primary slot at one cell, as the
    // CombatOrder attack_ground order does; apply runs that order. Room
    // occupancy, firebreak and stock are Go policy (policy.BurnOccupied), not
    // native vetoes. Applied means the throw order was taken; the fire catching
    // is the next census. The attack_ground path calls no validator that
    // reads Event.current.
    internal sealed class IgniteActionHandler : IActionHandler
    {
        private static Common.Failure Refuse(string guard, string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Ignite guard " + guard + ": " + detail);

        private static Common.Failure? Resolve(Operations.IgniteIntent? intent, Common.ObservationContext context, out Pawn? pawn, out IntVec3 cell)
        {
            pawn = null; cell = IntVec3.Invalid;
            if (intent == null || intent.PawnId is null || !ProtoBoundary.IsIdentifier(intent.PawnId) || intent.Cell == null || intent.Cell.X is null || intent.Cell.Z is null
                || intent.Cell.X.GetValueOrDefault() < 0 || intent.Cell.Z.GetValueOrDefault() < 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An ignite requires a pawn id and a target cell.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.mapPawns.FreeColonistsSpawned.ById(intent.PawnId);
            if (found == null) return Refuse("pawn", "The colonist is not spawned on this map.");
            if (found.Dead || found.Downed || found.InMentalState) return Refuse("pawn", "The colonist is dead, downed or in a mental state.");
            if (found.drafter == null || !found.Drafted) return Refuse("drafted", "The colonist is not drafted.");
            var verb = found.equipment?.PrimaryEq?.PrimaryVerb;
            if ((verb as Verb_LaunchProjectile)?.Projectile?.projectile?.ai_IsIncendiary != true)
                return Refuse("molotov", "The colonist holds no molotov in the primary slot.");
            cell = new IntVec3(intent.Cell.X.GetValueOrDefault(), 0, intent.Cell.Z.GetValueOrDefault());
            if (!cell.InBounds(map)) return Refuse("cell", "The cell is off the map.");
            var room = cell.GetRoom(map);
            if (room == null) return Refuse("room", "The cell is in no room.");
            pawn = found;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Intent?.Ignite, context, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Intent?.Ignite, context, out var pawn, out var cell);
            if (failure != null || pawn == null)
                throw new ApplyRefusedException(failure ?? ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Ignite prerequisites changed before apply."));
            var command = new Operations.CombatOrders();
            command = command with { Orders = [.. command.Orders, new Operations.CombatOrder
            {
                Pawn = new Operations.EntityPrecondition { EntityId = pawn.GetUniqueLoadID() },
                Order = new Operations.CombatOrderOrder.AttackGroundCase(new Common.Cell { X = cell.x, Z = cell.z }),
            }] };
            var evidence = NativeCombatOrders.Apply(command, context);
            var results = evidence.Effect?.CombatOrders?.Results;
            var result = results is { Count: > 0 } rs ? rs[0] : null;
            if (result?.Applied != true)
                throw new ApplyRefusedException(Common.FailureCode.InvalidRequest, "Ignite guard throw: the order was refused (" + result?.Refusal + ").");
            return evidence;
        }
    }
}
