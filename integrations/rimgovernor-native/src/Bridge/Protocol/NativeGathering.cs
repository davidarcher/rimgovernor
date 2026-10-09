#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The GatheringIntent arm of Actions/Apply: starts one vanilla gathering
    // (a party) through GatheringDef.Worker.TryExecute(map, organizer). The
    // game picks the spot (a built PartySpot controls it), so the intent
    // carries none and the effect records the one the lord job took. Native
    // refuses when the organizer may not start a gathering, or when the def's
    // own CanExecute fails (game conditions, no spot). An immediate write with
    // no native job; a gathering of the def already running applies again.
    internal static class NativeGathering
    {
        private sealed class Plan
        {
            public GatheringDef Def = null!;
            public Pawn Organizer = null!;
            public LordJob_Joinable_Gathering? Running;
        }

        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);

        private static LordJob_Joinable_Gathering? FindRunning(GatheringDef def) =>
            Verse.Find.Maps.SelectMany(map => map.lordManager.lords)
                .Select(lord => lord.LordJob as LordJob_Joinable_Gathering)
                .FirstOrDefault(job => job != null && Traverse.Create(job).Field("gatheringDef").GetValue<GatheringDef>() == def);

        private static Common.Failure? Resolve(Operations.GatheringIntent? command, out Plan? plan)
        {
            plan = null;
            if (command == null || !command.HasGatheringDef || !ProtoBoundary.IsIdentifier(command.GatheringDef) || command.Organizer == null || !ProtoBoundary.IsIdentifier(command.Organizer.Id))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A gathering command requires a gathering def and an organizer.");
            var def = DefDatabase<GatheringDef>.GetNamedSilentFail(command.GatheringDef);
            if (def == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Unknown gathering def.");
            var organizer = PawnsFinder.AllMaps_FreeColonists.ById(command.Organizer.Id);
            if (organizer == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist organizer is unavailable.");
            var found = new Plan { Def = def, Organizer = organizer, Running = FindRunning(def) };
            plan = found;
            if (found.Running != null) return null;
            var map = organizer.MapHeld;
            if (!organizer.Spawned || map == null) return Invalid("The organizer is not on a map.");
            if (!GatheringsUtility.PawnCanStartOrContinueGathering(organizer))
                return Invalid("The organizer cannot start or continue a gathering.");
            if (!GatheringsUtility.AcceptableGameConditionsToStartGathering(map, def))
                return Invalid("The game conditions do not allow a gathering now.");
            if (!def.CanExecute(map, organizer))
                return Invalid("The game finds no spot or organizer for this gathering.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.GatheringIntent command, Plan plan)
        {
            var job = plan.Running ?? FindRunning(plan.Def);
            if (job == null) throw new InvalidOperationException("Native gathering start readback did not apply.");
            return new Receipts.EffectEvidence
            {
                Gathering = new Receipts.GatheringEffect
                {
                    GatheringDef = command.GatheringDef,
                    OrganizerId = (job.Organizer ?? plan.Organizer).GetUniqueLoadID(),
                    Spot = new Common.Cell { X = job.Spot.x, Z = job.Spot.z }
                }
            };
        }

        internal static Common.Failure? Validate(Operations.GatheringIntent command) => Resolve(command, out _);

        internal static Receipts.EffectEvidence Apply(Operations.GatheringIntent command)
        {
            var failure = Resolve(command, out var plan);
            if (failure != null || plan == null) throw new InvalidOperationException("Gathering start prerequisites changed before apply: " + failure?.Detail);
            if (plan.Running != null) return Evidence(command, plan);
            if (!plan.Def.Worker.TryExecute(plan.Organizer.MapHeld, plan.Organizer))
                throw new InvalidOperationException("The game declined to start the gathering.");
            return Evidence(command, plan);
        }
    }

    internal sealed class GatheringActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeGathering.Validate(action.Gathering);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeGathering.Apply(action.Gathering);
    }
}
