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
    // GiveJobIntent UseItem (#1038, #1352): one player-controlled
    // colonist uses one item. Three native shapes are generic here:
    //  - an item the pawn wears or equips whose verb is a
    //    Verb_CastTargetEffect (the psychic shock and insanity lances): the
    //    verb's own Available/ValidateTarget checks, then its
    //    OrderForceTarget, which is exactly the worn gizmo's order;
    //  - a CompTargetable item with CompUsable: CanBeUsedBy and the comp's
    //    own target check, then the use job the float menu starts.
    //  - the pawn as its own target (#1609): a CompUsable item without a
    //    CompTargetable (a neuroformer, a psycast neurotrainer): CanBeUsedBy,
    //    then the comp's use job on the item alone, as the float menu does.
    // A pawn already running the job on that target with that item applies
    // again. Applied means ordered; the target's hediff is the next census.
    internal static class NativeUseItemOperations
    {
        private static readonly AccessTools.FieldRef<CompTargetable, Thing> SelectedTarget = AccessTools.FieldRefAccess<CompTargetable, Thing>("selectedTarget");

        private static Common.Failure Fail(Common.FailureCode code, string detail) => ProtoBoundary.Fail(code, detail);

        // The verb of an item the pawn wears or equips, when it is a target-effect verb.
        private static Verb? HeldVerb(Pawn pawn, string itemId, out Thing? item)
        {
            item = (Thing?)pawn.apparel?.WornApparel.ById(itemId)
                ?? pawn.equipment?.AllEquipmentListForReading.ById(itemId);
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
        // targets the item (targetA) and the extra target (targetB), none for
        // a self use.
        private static bool Running(Pawn pawn, Thing item, Pawn target, Verb? verb)
        {
            var job = pawn.CurJob;
            if (job == null) return false;
            return verb != null ? job.verbToUse == verb && job.targetA.Thing == target
                : job.targetA.Thing == item && (pawn == target || job.targetB.Thing == target) && job.def == (item as ThingWithComps)?.TryGetComp<CompUsable>()?.Props.useJob;
        }

        private static Common.Failure? Resolve(string pawnId, string itemId, string targetId, Common.ObservationContext context, out Pawn? pawn, out Thing? item, out Pawn? target, out Verb? verb, out CompUsable? usable)
        {
            pawn = null; item = null; target = null; verb = null; usable = null;
            if (!ProtoBoundary.IsIdentifier(pawnId) || !ProtoBoundary.IsIdentifier(itemId) || !ProtoBoundary.IsIdentifier(targetId) || itemId == pawnId || itemId == targetId)
                return Fail(Common.FailureCode.InvalidRequest, "Use item requires pawn, item and target ids, the item distinct from both.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.FreeColonistsSpawned.ById(pawnId);
            if (pawn == null) return Fail(Common.FailureCode.NotFound, "Exact colonist is not spawned on this map.");
            if (pawn.Dead || pawn.Downed || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return Fail(Common.FailureCode.InvalidRequest, "Colonist is not player-controlled and able.");
            target = map.mapPawns.AllPawnsSpawned.ById(targetId);
            if (target == null) return Fail(Common.FailureCode.NotFound, "Exact target pawn is not spawned on this map.");
            if (target.Dead) return Fail(Common.FailureCode.InvalidRequest, "Target is dead.");
            var self = pawn == target;
            verb = self ? null : HeldVerb(pawn, itemId, out item);
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
            var thing = RefIndex.Thing(map, itemId)
                ?? pawn.inventory?.innerContainer.ById(itemId);
            item = thing;
            var targetable = (thing as ThingWithComps)?.GetComps<CompTargetable>().FirstOrDefault();
            usable = (thing as ThingWithComps)?.TryGetComp<CompUsable>();
            if (thing == null) return Fail(Common.FailureCode.NotFound, "Exact item is not held by the colonist nor on this map.");
            if (self ? targetable != null || usable == null : targetable == null || usable == null)
                return Fail(Common.FailureCode.InvalidRequest, self ? "Self use needs a usable item without a target comp." : "Item has no target verb and no targetable use.");
            if (Running(pawn, thing, target, null)) return null;
            var report = usable.CanBeUsedBy(pawn, usable.Props.ignoreOtherReservations);
            if (!report.Accepted) return Fail(Common.FailureCode.InvalidRequest, "Colonist cannot use the item: " + (report.Reason ?? ""));
            if (!self && !targetable!.CanHitTarget(target)) return Fail(Common.FailureCode.InvalidRequest, "Item refuses this target.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Thing targetA, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = targetA.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = pawn.Drafted,
            }
        };

        internal static Common.Failure? Validate(string pawnId, string itemId, string targetId, Common.ObservationContext context) => Resolve(pawnId, itemId, targetId, context, out _, out _, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(string pawnId, string itemId, string targetId, Common.ObservationContext context)
        {
            var failure = Resolve(pawnId, itemId, targetId, context, out var pawn, out var item, out var target, out var verb, out var usable);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            // The job's targetA: the pawn for a verb, the item for a use job.
            Thing TargetA() => verb != null ? target! : item!;
            if (Running(pawn!, item!, target!, verb)) return Evidence(pawn!, TargetA(), pawn!.CurJob, false);
            if (verb != null) verb.OrderForceTarget(target);
            else if (pawn == target) usable!.TryStartUseJob(pawn!, LocalTargetInfo.Invalid, usable.Props.ignoreOtherReservations);
            else
            {
                SelectedTarget((CompTargetable)((ThingWithComps)item!).GetComps<CompTargetable>().First()) = target!;
                usable!.TryStartUseJob(pawn!, target, usable.Props.ignoreOtherReservations);
            }
            if (!Running(pawn!, item!, target!, verb)) throw new InvalidOperationException("The colonist did not take the use job.");
            return Evidence(pawn!, TargetA(), pawn!.CurJob, true);
        }
    }
}
