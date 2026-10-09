#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The AbilityIntent arm of Actions/Apply: one pawn uses one ability
    // of one source on one target. The shared part checks the pawn and the
    // target arm's shape; each source has a registered IAbilitySource whose
    // named guards decide eligibility from live state through the game's own
    // validation, and whose use is the game's own entry point. Favor cost and
    // cooldown are read here, never taken from the controller.
    internal interface IAbilitySource
    {
        // The refusal of the first failing guard (its name leads the detail),
        // else null with the prepared use. The prepared use is the only thing
        // that changes game state, and Check changes none.
        Common.Failure? Check(Pawn pawn, Operations.AbilityIntent intent, out Func<Receipts.EffectEvidence>? use);
    }

    internal static class NativeAbilityOperations
    {
        // One source per AbilityIntent.source arm, each with its own guards.
        private static readonly Dictionary<Operations.AbilityIntent.SourceOneofCase, IAbilitySource> Sources = new Dictionary<Operations.AbilityIntent.SourceOneofCase, IAbilitySource>
        {
            [Operations.AbilityIntent.SourceOneofCase.Permit] = new PermitAbilitySource(),
            [Operations.AbilityIntent.SourceOneofCase.Psycast] = new PsycastAbilitySource(),
        };

        internal static Common.Failure Refuse(Common.FailureCode code, string guard, string detail) => ProtoBoundary.Fail(code, "Ability guard " + guard + ": " + detail);

        // The caller: alive, spawned, a free colonist, not downed. Drafted is allowed.
        private static Common.Failure? Caller(Operations.AbilityIntent intent, out Pawn? pawn)
        {
            pawn = null;
            if (!intent.HasPawnId || !ProtoBoundary.IsIdentifier(intent.PawnId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An ability use requires a pawn id.");
            var found = global::Verse.Find.Maps.SelectMany(m => m.mapPawns.AllPawns).ById(intent.PawnId);
            if (found == null || !found.Spawned)
                return Refuse(Common.FailureCode.NotFound, "pawn", "The pawn is not spawned on a map.");
            if (found.Dead || !found.IsFreeColonist)
                return Refuse(Common.FailureCode.InvalidRequest, "pawn", "The pawn is not a living free colonist.");
            if (found.Downed)
                return Refuse(Common.FailureCode.InvalidRequest, "pawn", "The pawn is downed.");
            pawn = found;
            return null;
        }

        private static Common.Failure? Resolve(Operations.AbilityIntent? intent, out Func<Receipts.EffectEvidence>? use)
        {
            use = null;
            if (intent == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An ability use is required.");
            if (!Sources.TryGetValue(intent.SourceCase, out var source))
                return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This ability source is not supported.");
            if (intent.TargetCase == Operations.AbilityIntent.TargetOneofCase.None)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An ability use requires a target arm (none, cell, pawn or thing).");
            var failure = Caller(intent, out var pawn);
            if (failure != null || pawn == null) return failure;
            return source.Check(pawn, intent, out use);
        }

        internal static Common.Failure? Validate(Operations.AbilityIntent? intent) => Resolve(intent, out _);

        internal static Receipts.EffectEvidence Apply(Operations.AbilityIntent? intent)
        {
            var failure = Resolve(intent, out var use);
            if (failure != null || use == null)
                throw new ApplyRefusedException(failure?.Code ?? Common.FailureCode.NativeFailure, failure?.Detail ?? "Ability prerequisites changed before apply.");
            return use();
        }
    }

    // The permit source: an acting royal permit (royalAid) the pawn holds with a
    // faction, used at a cell. The guards run in the order they are listed and
    // the game's own worker supplies the validation: its aid options carry the
    // hostile/defunct faction, map, temperature and favor checks, and its
    // ValidateTarget and CanHitTarget the targeting range, both read live. Only
    // targeted workers (CallAid, OrbitalStrike, DropResources, CallShuttle) are
    // supported; others are refused as Unsupported.
    internal sealed class PermitAbilitySource : IAbilitySource
    {
        private sealed class Call
        {
            internal Pawn Pawn = null!;
            internal Map Map = null!;
            internal RoyalTitlePermitDef Def = null!;
            internal FactionPermit Held = null!;
            internal Faction Faction = null!;
            internal RoyalTitlePermitWorker_Targeted Worker = null!;
            internal Operations.AbilityIntent Intent = null!;
            internal IntVec3 Cell;
        }

        private sealed class Guard
        {
            internal string Name = "";
            internal Func<Call, Common.Failure?> Check = null!;
        }

        // The named guard registry of the permit source. Names lead the refusal
        // detail and are documented in action-contracts.md.
        private static readonly Guard[] Guards =
        {
            new Guard { Name = "held", Check = call => call.Held.Permit != call.Def
                ? NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "held", "The pawn does not hold this permit for the faction.") : null },
            new Guard { Name = "cooldown", Check = call => call.Held.OnCooldown
                ? NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "cooldown", "The permit is on cooldown.") : null },
            new Guard { Name = "faction", Check = call => call.Faction.defeated || call.Faction.HostileTo(Faction.OfPlayer)
                ? NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "faction", "The faction is hostile or defunct.") : null },
            new Guard { Name = "favor", Check = call => call.Pawn.royalty.GetFavor(call.Faction) < call.Def.royalAid.favorCost
                ? NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "favor", "Favor is below the permit's cost.") : null },
            new Guard { Name = "aid", Check = call =>
            {
                // The worker's own options: a disabled one carries the game's reason.
                var options = call.Worker.GetRoyalAidOptions(call.Map, call.Pawn, call.Faction)?.ToList() ?? new List<FloatMenuOption>();
                if (options.Count == 0)
                    return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "aid", "The permit offers no aid here.");
                return options.Any(o => !o.Disabled) ? null
                    : NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "aid", options[0].Label ?? "The map cannot receive this aid.");
            } },
            new Guard { Name = "target", Check = call =>
            {
                if (call.Intent.TargetCase != Operations.AbilityIntent.TargetOneofCase.Cell || call.Intent.Cell == null || !call.Intent.Cell.HasX || !call.Intent.Cell.HasZ)
                    return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "target", "This permit needs a target cell.");
                var cell = new IntVec3(call.Intent.Cell.X, 0, call.Intent.Cell.Z);
                if (!cell.InBounds(call.Map))
                    return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "target", "The target cell is outside the map.");
                if (cell.Fogged(call.Map))
                    return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "target", "The target cell is fogged.");
                if (!cell.InHorDistOf(call.Pawn.Position, call.Def.royalAid.targetingRange)
                    || call.Def.royalAid.targetingRequireLOS && !GenSight.LineOfSight(call.Pawn.Position, cell, call.Map))
                    return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "target", "The target cell is out of the permit's targeting range.");
                call.Cell = cell;
                return null;
            } },
        };

        public Common.Failure? Check(Pawn pawn, Operations.AbilityIntent intent, out Func<Receipts.EffectEvidence>? use)
        {
            use = null;
            var source = intent.Permit;
            if (source == null || !source.HasFactionDef || !source.HasPermit || !ProtoBoundary.IsIdentifier(source.FactionDef) || !ProtoBoundary.IsIdentifier(source.Permit))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A permit ability requires a faction def and a permit def.");
            var def = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(source.Permit);
            if (def == null)
                return NativeAbilityOperations.Refuse(Common.FailureCode.NotFound, "permit", "The permit is not defined.");
            if (def.royalAid == null)
                return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "permit", "The permit does not act (no royal aid).");
            if (!(def.Worker is RoyalTitlePermitWorker_Targeted worker))
                return NativeAbilityOperations.Refuse(Common.FailureCode.Unsupported, "permit", "Only targeted permit workers are supported.");
            if (pawn.royalty == null)
                return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "held", "The pawn holds no royal permit.");
            var held = pawn.royalty.AllFactionPermits.FirstOrDefault(p => p.Permit == def && p.Faction?.def?.defName == source.FactionDef);
            if (held == null)
                return NativeAbilityOperations.Refuse(Common.FailureCode.InvalidRequest, "held", "The pawn does not hold this permit for the faction.");
            var call = new Call { Pawn = pawn, Map = pawn.Map, Def = def, Held = held, Faction = held.Faction, Worker = worker, Intent = intent };
            foreach (var guard in Guards)
            {
                var refusal = guard.Check(call);
                if (refusal != null) return refusal;
            }
            use = () => Use(call);
            return null;
        }

        // The game's own call: the worker's enabled aid option begins targeting,
        // the worker validates the target, and OrderForceTarget runs the call
        // (spending favor and starting the cooldown). The player's targeter is
        // always stopped afterwards. The receipt is the read-back postcondition.
        private static Receipts.EffectEvidence Use(Call call)
        {
            var tick = Find.TickManager.TicksGame;
            var favorBefore = call.Pawn.royalty.GetFavor(call.Faction);
            var option = (call.Worker.GetRoyalAidOptions(call.Map, call.Pawn, call.Faction) ?? Enumerable.Empty<FloatMenuOption>()).FirstOrDefault(o => !o.Disabled && o.action != null);
            if (option == null)
                throw new ApplyRefusedException(Common.FailureCode.InvalidRequest, "Ability guard aid: the permit offers no enabled aid option.");
            var target = new LocalTargetInfo(call.Cell);
            try
            {
                option.action();
                if (!call.Worker.ValidateTarget(target, false) || !call.Worker.CanHitTarget(target))
                    throw new ApplyRefusedException(Common.FailureCode.InvalidRequest, "Ability guard target: the permit worker refuses the target cell.");
                call.Worker.OrderForceTarget(target);
            }
            finally
            {
                Find.Targeter.StopTargeting();
            }
            var favorAfter = call.Pawn.royalty.GetFavor(call.Faction);
            if (call.Held.LastUsedTick != tick || favorAfter != favorBefore - call.Def.royalAid.favorCost)
                throw new InvalidOperationException("Permit call readback did not start the cooldown and spend the favor.");
            return new Receipts.EffectEvidence
            {
                Ability = new Receipts.AbilityEffect
                {
                    PawnId = call.Pawn.GetUniqueLoadID(),
                    Permit = new Receipts.PermitUseEffect
                    {
                        FactionDef = call.Faction.def.defName, Permit = call.Def.defName, CooldownStartedTick = call.Held.LastUsedTick,
                        FavorBefore = favorBefore, FavorAfter = favorAfter,
                    },
                },
            };
        }
    }

    // The psycast source: a psycast the pawn knows, aimed at the arm the
    // ability takes. The guards run in the order listed; target, cast, range and
    // confirmation call the game's own validation (CanApplyOn,
    // Verb.ValidateTarget, Ability.CanCast, GizmoDisabled, Verb.CanHitTarget),
    // and psyfocus and entropy read the pawn's tracker live. The use is the
    // game's own player order, Ability.QueueCastingJob, so the cast, its
    // psyfocus cost and its cooldown follow when the cast job runs. The receipt
    // asserts the read-back postcondition: the pawn holds the ability's cast job.
    internal sealed class PsycastAbilitySource : IAbilitySource
    {
        private const float PsyfocusTolerance = 0.001f;

        private sealed class Cast
        {
            internal Pawn Pawn = null!;
            internal Ability Ability = null!;
            internal Operations.AbilityIntent Intent = null!;
            internal LocalTargetInfo Target;
            internal bool SelfTarget;
        }

        private sealed class Guard
        {
            internal string Name = "";
            internal Func<Cast, Common.Failure?> Check = null!;
        }

        private static Common.Failure Refuse(string guard, string detail, Common.FailureCode code = Common.FailureCode.InvalidRequest) => NativeAbilityOperations.Refuse(code, guard, detail);

        private static bool SelfOnly(TargetingParameters t) => t.canTargetSelf && !t.canTargetPawns && !t.canTargetLocations && !t.canTargetBuildings && !t.canTargetItems;

        private static bool Ordered(Cast c) => c.Ability.Casting || c.Pawn.jobs != null && c.Pawn.jobs.AllJobs().Any(j => j.ability == c.Ability);

        // The named guard registry of the psycast source. Names lead the refusal
        // detail and are documented in action-contracts.md.
        private static readonly Guard[] Guards =
        {
            new Guard { Name = "cooldown", Check = c => c.Ability.OnCooldown
                ? Refuse("cooldown", "The psycast is on cooldown.") : null },
            new Guard { Name = "casting", Check = c => Ordered(c)
                ? Refuse("casting", "The pawn is already ordered to cast this psycast.") : null },
            new Guard { Name = "target", Check = ResolveTarget },
            new Guard { Name = "psyfocus", Check = c =>
            {
                var tracker = c.Pawn.psychicEntropy;
                if (tracker == null || tracker.Psylink == null)
                    return Refuse("psyfocus", "The pawn has no psylink.");
                return tracker.NeedsPsyfocus && tracker.CurrentPsyfocus < c.Ability.FinalPsyfocusCost(c.Target) - PsyfocusTolerance
                    ? Refuse("psyfocus", "Psyfocus is below the psycast's cost.") : null;
            } },
            new Guard { Name = "entropy", Check = c => c.Pawn.psychicEntropy.WouldOverflowEntropy(c.Ability.def.EntropyGain)
                ? Refuse("entropy", "The cast would overflow the pawn's neural heat.") : null },
            new Guard { Name = "cast", Check = c =>
            {
                var can = c.Ability.CanCast;
                if (!can.Accepted)
                    return Refuse("cast", string.IsNullOrEmpty(can.Reason) ? "The game refuses the cast." : can.Reason);
                return c.Ability.GizmoDisabled(out var reason)
                    ? Refuse("cast", string.IsNullOrEmpty(reason) ? "The game disables this psycast." : reason) : null;
            } },
            new Guard { Name = "range", Check = c => c.SelfTarget || c.Ability.verb.CanHitTarget(c.Target)
                ? null : Refuse("range", "The target is out of range or out of sight of the caster.") },
            new Guard { Name = "confirmation", Check = c => c.Ability.ConfirmationDialog(c.Target, () => { }) != null
                ? Refuse("confirmation", "The game asks for a confirmation for this cast.", Common.FailureCode.Unsupported) : null },
        };

        // The arm the ability takes, resolved to the game's target; the game's
        // own CanApplyOn and ValidateTarget have the last word.
        private static Common.Failure? ResolveTarget(Cast c)
        {
            var verb = c.Ability.verb;
            var t = verb?.targetParams;
            if (verb == null || t == null)
                return Refuse("target", "The psycast has no targeting parameters.", Common.FailureCode.Unsupported);
            if (c.Ability.def.targetWorldCell || c.Ability.EffectComps.Any(e => e is CompAbilityEffect_WithDest))
                return Refuse("target", "A psycast with a destination or a world target is not supported.", Common.FailureCode.Unsupported);
            var map = c.Pawn.Map;
            switch (c.Intent.TargetCase)
            {
                case Operations.AbilityIntent.TargetOneofCase.NoTarget:
                    if (!SelfOnly(t) && c.Ability.def.targetRequired)
                        return Refuse("target", "This psycast needs a target (pawn, thing or cell).");
                    c.Target = new LocalTargetInfo(c.Pawn);
                    c.SelfTarget = true;
                    break;
                case Operations.AbilityIntent.TargetOneofCase.Pawn:
                {
                    if (!ProtoBoundary.IsIdentifier(c.Intent.Pawn))
                        return Refuse("target", "The target pawn id is invalid.");
                    var pawn = map.mapPawns.AllPawnsSpawned.ById(c.Intent.Pawn);
                    if (pawn == null)
                        return Refuse("target", "The target pawn is not spawned on the caster's map.", Common.FailureCode.NotFound);
                    if (!t.canTargetPawns && !(t.canTargetSelf && pawn == c.Pawn))
                        return Refuse("target", "This psycast does not take a pawn target.");
                    c.Target = new LocalTargetInfo(pawn);
                    c.SelfTarget = pawn == c.Pawn;
                    break;
                }
                case Operations.AbilityIntent.TargetOneofCase.Thing:
                {
                    if (!ProtoBoundary.IsIdentifier(c.Intent.Thing))
                        return Refuse("target", "The target thing id is invalid.");
                    var thing = map.listerThings.AllThings.ById(c.Intent.Thing);
                    if (thing == null || !thing.Spawned)
                        return Refuse("target", "The target thing is not spawned on the caster's map.", Common.FailureCode.NotFound);
                    if (thing is Pawn || !t.canTargetBuildings && !t.canTargetItems)
                        return Refuse("target", "This psycast does not take a thing target.");
                    c.Target = new LocalTargetInfo(thing);
                    break;
                }
                case Operations.AbilityIntent.TargetOneofCase.Cell:
                {
                    if (c.Intent.Cell == null || !c.Intent.Cell.HasX || !c.Intent.Cell.HasZ)
                        return Refuse("target", "The target cell is incomplete.");
                    if (!t.canTargetLocations)
                        return Refuse("target", "This psycast does not take a cell target.");
                    var cell = new IntVec3(c.Intent.Cell.X, 0, c.Intent.Cell.Z);
                    if (!cell.InBounds(map))
                        return Refuse("target", "The target cell is outside the map.");
                    if (cell.Fogged(map))
                        return Refuse("target", "The target cell is fogged.");
                    c.Target = new LocalTargetInfo(cell);
                    break;
                }
                default:
                    return Refuse("target", "A target arm is required.");
            }
            return c.Ability.CanApplyOn(c.Target) && verb.ValidateTarget(c.Target, false)
                ? null : Refuse("target", "The game refuses this target for the psycast.");
        }

        public Common.Failure? Check(Pawn pawn, Operations.AbilityIntent intent, out Func<Receipts.EffectEvidence>? use)
        {
            use = null;
            var source = intent.Psycast;
            if (source == null || !source.HasAbility || !ProtoBoundary.IsIdentifier(source.Ability))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A psycast ability requires an ability def.");
            var def = DefDatabase<AbilityDef>.GetNamedSilentFail(source.Ability);
            if (def == null || !def.IsPsycast)
                return Refuse("ability", "The ability is not a defined psycast.", Common.FailureCode.NotFound);
            var ability = pawn.abilities?.GetAbility(def, false);
            if (ability == null)
                return Refuse("ability", "The pawn does not know this psycast.");
            var cast = new Cast { Pawn = pawn, Ability = ability, Intent = intent };
            foreach (var guard in Guards)
            {
                var refusal = guard.Check(cast);
                if (refusal != null) return refusal;
            }
            use = () => Use(cast);
            return null;
        }

        // The player's own order; the read-back is the postcondition.
        private static Receipts.EffectEvidence Use(Cast cast)
        {
            cast.Ability.QueueCastingJob(cast.Target, LocalTargetInfo.Invalid);
            var job = cast.Pawn.jobs?.AllJobs().FirstOrDefault(j => j.ability == cast.Ability);
            if (job == null)
                throw new InvalidOperationException("Psycast order readback found no cast job on the pawn.");
            return new Receipts.EffectEvidence
            {
                Ability = new Receipts.AbilityEffect
                {
                    PawnId = cast.Pawn.GetUniqueLoadID(),
                    Psycast = new Receipts.PsycastUseEffect { Ability = cast.Ability.def.defName, JobDef = job.def.defName },
                },
            };
        }
    }

    internal sealed class AbilityActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeAbilityOperations.Validate(action.Ability);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeAbilityOperations.Apply(action.Ability);
    }
}
