#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The UseItemIntent arm of Actions/Apply (#1038): one player-controlled
    // colonist uses one targetable item on one spawned pawn. Two native
    // shapes are generic here:
    //  - an item the pawn wears or equips whose verb is a
    //    Verb_CastTargetEffect (the psychic shock and insanity lances): the
    //    verb's own Available/ValidateTarget checks, then its
    //    OrderForceTarget, which is exactly the worn gizmo's order;
    //  - a CompTargetable item with CompUsable: CanBeUsedBy and the comp's
    //    own target check, then the use job the float menu starts.
    // A pawn already running the job on that target with that item applies
    // again. Applied means ordered; the target's hediff is the next census.
    internal static class NativeUseItemOperations
    {
        private static readonly AccessTools.FieldRef<CompTargetable, Thing> SelectedTarget = AccessTools.FieldRefAccess<CompTargetable, Thing>("selectedTarget");

        private static Common.Failure Fail(Common.FailureCode code, string detail) => ProtoBoundary.Fail(code, detail);

        // The verb of an item the pawn wears or equips, when it is a target-effect verb.
        private static Verb? HeldVerb(Pawn pawn, string itemId, out Thing? item)
        {
            item = (Thing?)pawn.apparel?.WornApparel.FirstOrDefault(a => a.GetUniqueLoadID() == itemId)
                ?? pawn.equipment?.AllEquipmentListForReading.FirstOrDefault(e => e.GetUniqueLoadID() == itemId);
            if (item is not ThingWithComps owner) return null;
            var verbs = owner.TryGetComp<CompApparelVerbOwner>()?.AllVerbs ?? owner.TryGetComp<CompEquippable>()?.AllVerbs;
            return verbs?.FirstOrDefault(v => v is Verb_CastTargetEffect);
        }

        // Verb_CastBase.ValidateTarget reads Event.current.shift (ReloadableUtility
        // .CanUseConsideringQueuedJobs), which is null outside OnGUI: give it a
        // plain event (no shift = the charge check) for the call. The field, not
        // the setter: the setter pushes a native event and refuses null.
        private static readonly AccessTools.FieldRef<UnityEngine.Event> CurrentEvent = AccessTools.StaticFieldRefAccess<UnityEngine.Event>(AccessTools.Field(typeof(UnityEngine.Event), "s_Current"));
        private static readonly UnityEngine.Event PlainEvent = new UnityEngine.Event();

        private static bool ValidateOffGui(Verb verb, Pawn target)
        {
            if (CurrentEvent() != null) return verb.ValidateTarget(target, false);
            CurrentEvent() = PlainEvent;
            try { return verb.ValidateTarget(target, false); }
            finally { CurrentEvent() = null!; }
        }

        // The verb's job targets the pawn (targetA); CompUsable's use job
        // targets the item (targetA) and the extra target (targetB).
        private static bool Running(Pawn pawn, Thing item, Pawn target, Verb? verb)
        {
            var job = pawn.CurJob;
            if (job == null) return false;
            return verb != null ? job.verbToUse == verb && job.targetA.Thing == target
                : job.targetA.Thing == item && job.targetB.Thing == target && job.def == (item as ThingWithComps)?.TryGetComp<CompUsable>()?.Props.useJob;
        }

        private static Common.Failure? Resolve(Operations.UseItemIntent? intent, Common.ObservationContext context, out Pawn? pawn, out Thing? item, out Pawn? target, out Verb? verb, out CompUsable? usable)
        {
            pawn = null; item = null; target = null; verb = null; usable = null;
            if (intent == null || !ProtoBoundary.IsIdentifier(intent.PawnId) || !ProtoBoundary.IsIdentifier(intent.ItemId) || !ProtoBoundary.IsIdentifier(intent.TargetId) || intent.PawnId == intent.TargetId)
                return Fail(Common.FailureCode.InvalidRequest, "Use item requires distinct pawn, item and target ids.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == intent.PawnId);
            if (pawn == null) return Fail(Common.FailureCode.NotFound, "Exact colonist is not spawned on this map.");
            if (pawn.Dead || pawn.Downed || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return Fail(Common.FailureCode.InvalidRequest, "Colonist is not player-controlled and able.");
            target = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == intent.TargetId);
            if (target == null) return Fail(Common.FailureCode.NotFound, "Exact target pawn is not spawned on this map.");
            if (target.Dead) return Fail(Common.FailureCode.InvalidRequest, "Target is dead.");
            verb = HeldVerb(pawn, intent.ItemId, out item);
            if (verb != null)
            {
                if (Running(pawn, item!, target, verb)) return null;
                if (verb.verbProps.violent && pawn.WorkTagIsDisabled(WorkTags.Violent))
                    return Fail(Common.FailureCode.InvalidRequest, "Colonist is incapable of violence.");
                if (verb.caster != pawn) verb.caster = pawn;
                if (!verb.Available()) return Fail(Common.FailureCode.InvalidRequest, "Item verb has no charge or cannot be used here.");
                if (!verb.verbProps.targetParams.CanTarget(target) || !ValidateOffGui(verb, target))
                    return Fail(Common.FailureCode.InvalidRequest, "Item verb refuses this target.");
                return null;
            }
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == intent.ItemId)
                ?? pawn.inventory?.innerContainer.FirstOrDefault(t => t.GetUniqueLoadID() == intent.ItemId);
            item = thing;
            var targetable = (thing as ThingWithComps)?.GetComps<CompTargetable>().FirstOrDefault();
            usable = (thing as ThingWithComps)?.TryGetComp<CompUsable>();
            if (thing == null) return Fail(Common.FailureCode.NotFound, "Exact item is not held by the colonist nor on this map.");
            if (targetable == null || usable == null) return Fail(Common.FailureCode.InvalidRequest, "Item has no target verb and no targetable use.");
            if (Running(pawn, thing, target, null)) return null;
            var report = usable.CanBeUsedBy(pawn, usable.Props.ignoreOtherReservations);
            if (!report.Accepted) return Fail(Common.FailureCode.InvalidRequest, "Colonist cannot use the item: " + (report.Reason ?? ""));
            if (!targetable.CanHitTarget(target)) return Fail(Common.FailureCode.InvalidRequest, "Item refuses this target.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Pawn target, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = pawn.Drafted,
            }
        };

        internal static Common.Failure? Validate(Operations.UseItemIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.UseItemIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var item, out var target, out var verb, out var usable);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, item!, target!, verb)) return Evidence(pawn!, target!, pawn!.CurJob, false);
            if (verb != null) verb.OrderForceTarget(target);
            else
            {
                SelectedTarget((CompTargetable)((ThingWithComps)item!).GetComps<CompTargetable>().First()) = target!;
                usable!.TryStartUseJob(pawn!, target, usable.Props.ignoreOtherReservations);
            }
            if (!Running(pawn!, item!, target!, verb)) throw new InvalidOperationException("The colonist did not take the use job.");
            return Evidence(pawn!, target!, pawn!.CurJob, true);
        }
    }

    internal sealed class UseItemActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeUseItemOperations.Validate(action.UseItem, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeUseItemOperations.Apply(action.UseItem, context);
    }
}
