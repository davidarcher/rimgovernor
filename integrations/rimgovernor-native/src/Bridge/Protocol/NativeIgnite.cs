#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // IgniteIntent on Actions/Apply (#1815, epic #1640): one drafted colonist
    // force-fires the molotov in the primary slot at one cell, as the
    // CombatOrder attack_ground order does; apply runs that order. Refused
    // while any pawn (colonist, animal, prisoner or hostile, downed or not)
    // stands in the cell's room, the thrower included: the burn-out clears
    // everything in the room, so nothing alive may be in it. No firebreak or
    // stock veto. Applied means the throw order was taken; the fire catching
    // is the next census. The attack_ground path calls no validator that
    // reads Event.current (#1038).
    internal sealed class IgniteActionHandler : IActionHandler
    {
        private static Common.Failure Refuse(string guard, string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Ignite guard " + guard + ": " + detail);

        private static Common.Failure? Resolve(Operations.IgniteIntent? intent, Common.ObservationContext context, out Pawn? pawn, out IntVec3 cell)
        {
            pawn = null; cell = IntVec3.Invalid;
            if (intent == null || !intent.HasPawnId || !ProtoBoundary.IsIdentifier(intent.PawnId) || intent.Cell == null || !intent.Cell.HasX || !intent.Cell.HasZ
                || intent.Cell.X < 0 || intent.Cell.Z < 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An ignite requires a pawn id and a target cell.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.mapPawns.FreeColonistsSpawned.ById(intent.PawnId);
            if (found == null) return Refuse("pawn", "The colonist is not spawned on this map.");
            if (found.Dead || found.Downed || found.InMentalState) return Refuse("pawn", "The colonist is dead, downed or in a mental state.");
            if (found.drafter == null || !found.Drafted) return Refuse("drafted", "The colonist is not drafted.");
            var verb = found.equipment?.PrimaryEq?.PrimaryVerb;
            if ((verb as Verb_LaunchProjectile)?.Projectile?.projectile?.ai_IsIncendiary != true)
                return Refuse("molotov", "The colonist holds no molotov in the primary slot.");
            cell = new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
            if (!cell.InBounds(map)) return Refuse("cell", "The cell is off the map.");
            var room = cell.GetRoom(map);
            if (room == null) return Refuse("room", "The cell is in no room.");
            var occupant = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => !p.Dead && p.Position.GetRoom(map) == room);
            if (occupant != null) return Refuse("occupied", "A pawn is inside the target room.");
            pawn = found;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Ignite, context, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Ignite, context, out var pawn, out var cell);
            if (failure != null || pawn == null)
                throw new ApplyRefusedException(failure?.Code ?? Common.FailureCode.NativeFailure, failure?.Detail ?? "Ignite prerequisites changed before apply.");
            var command = new Operations.CombatOrders();
            command.Orders.Add(new Operations.CombatOrder
            {
                Pawn = new Operations.EntityPrecondition { EntityId = pawn.GetUniqueLoadID() },
                AttackGround = new Common.Cell { X = cell.x, Z = cell.z },
            });
            var evidence = NativeCombatOrders.Apply(command, context);
            var result = evidence.CombatOrders.Results[0];
            if (!result.Applied)
                throw new ApplyRefusedException(Common.FailureCode.InvalidRequest, "Ignite guard throw: the order was refused (" + result.Refusal + ").");
            return evidence;
        }
    }
}
