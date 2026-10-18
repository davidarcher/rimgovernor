#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // The ConversionIntent arm of Actions/Apply: the moral guide's Convert ability, ordered on one colonist of
    // another ideoligion. The ability is the one the guide's role grants (the effect comp is the game's
    // CompAbilityEffect_Convert, so no def name is typed). The guards run in the order listed and the game's own
    // validation has the last word: CanCast, GizmoDisabled, CanApplyOn and Verb.ValidateTarget. The use is the
    // player's own order, Ability.QueueCastingJob; the guide walks into the ability's range, so range is the
    // job's, and reachability is a guard. Charge is read live and never taken from the controller. A repeat
    // while the cast is ordered is refused by the "casting" guard.
    internal static class NativeConversionOperations
    {
        // Whether the role precept grants the Convert ability (the moral guide's).
        internal static bool Grants(PreceptDef role) =>
            role.grantedAbilities != null && role.grantedAbilities.Any(a => a.comps != null && a.comps.OfType<CompProperties_AbilityConvert>().Any());

        internal static Ability? ConvertAbility(Pawn pawn) =>
            pawn.abilities?.AllAbilitiesForReading.FirstOrDefault(a => a.EffectComps.Any(c => c is CompAbilityEffect_Convert));

        private static bool Ordered(Pawn pawn, Ability ability) =>
            ability.Casting || pawn.jobs != null && pawn.jobs.AllJobs().Any(j => j.ability == ability);

        // The ability is charged: held, off cooldown and not already ordered.
        internal static bool Charged(Pawn pawn) => ConvertAbility(pawn) is { } ability && !ability.OnCooldown && !Ordered(pawn, ability);

        private static Common.Failure Refuse(string guard, string detail, Common.FailureCode code = Common.FailureCode.InvalidRequest) =>
            NativeAbilityOperations.Refuse(code, guard, detail);

        private sealed class Cast
        {
            internal Pawn Guide = null!;
            internal Pawn Target = null!;
            internal Ability Ability = null!;
        }

        private static Pawn? Colonist(string? id) =>
            ProtoBoundary.IsIdentifier(id) ? global::Verse.Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).ById(id) : null;

        private static Common.Failure? Resolve(Operations.ConversionIntent? intent, out Cast? cast)
        {
            cast = null;
            if (intent == null || !ProtoBoundary.IsIdentifier(intent.GuideId) || !ProtoBoundary.IsIdentifier(intent.TargetId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A conversion requires a guide and a target pawn id.");
            if (!ModsConfig.IdeologyActive)
                return Refuse("ideology", "Ideology is not active.", Common.FailureCode.Unavailable);
            var guide = Colonist(intent.GuideId);
            if (guide == null || guide.Dead || guide.Downed)
                return Refuse("guide", "The guide is not a standing free colonist on a map.", Common.FailureCode.NotFound);
            var target = Colonist(intent.TargetId);
            if (target == null || target.Dead)
                return Refuse("target", "The target is not a living free colonist on a map.", Common.FailureCode.NotFound);
            var ability = ConvertAbility(guide);
            if (ability == null)
                return Refuse("ability", "The guide holds no Convert ability.");
            if (target.Map != guide.Map)
                return Refuse("target", "The target is on another map.");
            if (target.Ideo == null || guide.Ideo == null || target.Ideo == guide.Ideo)
                return Refuse("target", "The target already holds the guide's ideoligion.");
            if (ability.OnCooldown)
                return Refuse("cooldown", "The Convert ability is on cooldown.");
            if (Ordered(guide, ability))
                return Refuse("casting", "The guide is already ordered to convert.");
            var info = new LocalTargetInfo(target);
            var can = ability.CanCast;
            if (!can.Accepted)
                return Refuse("cast", string.IsNullOrEmpty(can.Reason) ? "The game refuses the cast." : can.Reason);
            if (ability.GizmoDisabled(out var reason))
                return Refuse("cast", string.IsNullOrEmpty(reason) ? "The game disables the Convert ability." : reason);
            if (!ability.CanApplyOn(info) || !ability.verb.ValidateTarget(info, false))
                return Refuse("target", "The game refuses this target for the Convert ability.");
            if (!guide.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return Refuse("range", "The guide cannot reach the target.");
            if (ability.ConfirmationDialog(info, () => { }) != null)
                return Refuse("confirmation", "The game asks for a confirmation for this cast.", Common.FailureCode.Unsupported);
            cast = new Cast { Guide = guide, Target = target, Ability = ability };
            return null;
        }

        internal static Common.Failure? Validate(Operations.ConversionIntent? intent) => Resolve(intent, out _);

        internal static Receipts.EffectEvidence Apply(Operations.ConversionIntent? intent)
        {
            var failure = Resolve(intent, out var cast);
            if (failure != null || cast == null)
                throw new ApplyRefusedException(failure ?? ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Conversion prerequisites changed before apply."));
            cast.Ability.QueueCastingJob(new LocalTargetInfo(cast.Target), LocalTargetInfo.Invalid);
            if (!Ordered(cast.Guide, cast.Ability))
                throw new InvalidOperationException("Conversion order readback found no cast job on the guide.");
            return new Receipts.EffectEvidence();
        }
    }

    internal sealed class ConversionActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeConversionOperations.Validate(action.Intent?.Conversion);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeConversionOperations.Apply(action.Intent?.Conversion);
    }
}
