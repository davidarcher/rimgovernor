using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimGovernor.Native;

internal static class NativeSupportSurgery
{
    internal static bool Resolve(Pawn doctor, Pawn patient, PawnActivityStep step, out Job? job)
    {
        job = null;
        if (step.Recipe is not { } choice || doctor == patient || !patient.InBed() || patient.Dead || patient.IsForbidden(doctor)
            || !doctor.CanReserveAndReach(patient, PathEndMode.InteractionCell, Danger.None)
            || DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is not WorkGiver_DoBill giver
            || !giver.ThingIsUsableBillGiver(patient) || giver.MissingRequiredCapacity(doctor) != null
            || doctor.WorkTagIsDisabled(giver.def.workTags) || doctor.WorkTypeIsDisabled(giver.def.workType)) return false;
        var bill = patient.BillStack.Bills.OfType<Bill_Medical>().SingleOrDefault(b => b.GetUniqueLoadID() == choice.Bill);
        if (bill == null || !bill.recipe.AvailableNow || !bill.recipe.AvailableOnNow(patient, bill.Part) || bill.recipe.defName != choice.Recipe || !bill.ShouldDoNow() || !bill.PawnAllowedToStartAnew(doctor)
            || bill.IsSurgeryViolationOnExtraFactionMember(doctor) || bill.recipe.FirstSkillRequirementPawnDoesntSatisfy(doctor) != null
            || bill.uniqueRequiredIngredients?.Count > 0 || bill.recipe.requiredGiverWorkType != null && bill.recipe.requiredGiverWorkType != giver.def.workType) return false;
        if (choice.Ingredients.Count == 0 || choice.Ingredients.Count > 16 || choice.Ingredients.Any(i => i.Count <= 0)
            || choice.Ingredients.Select(i => i.Thing).Distinct(StringComparer.Ordinal).Count() != choice.Ingredients.Count) return false;
        var inputs = new List<ThingCount>(); ThingDef? resource = null;
        foreach (var input in choice.Ingredients)
        {
            var thing = RefIndex.Thing(doctor.Map, input.Thing);
            if (thing == null || !thing.Spawned || thing.IsForbidden(doctor) || thing.stackCount < input.Count
                || resource != null && resource != thing.def || choice.Resource != thing.def.defName || !bill.IsFixedOrAllowedIngredient(thing)
                || thing.Position.DistanceTo(patient.Position) > bill.ingredientSearchRadius
                || thing.def.IsMedicine && !WorkGiver_DoBill.GetMedicalCareCategory(patient).AllowsMedicine(thing.def)
                || !doctor.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.None, stackCount: input.Count)) return false;
            resource = thing.def; inputs.Add(new(thing, input.Count));
        }
        if (resource == null || bill.recipe.ingredients.Any(i => !i.filter.Allows(resource))
            || inputs.Sum(i => i.Count) != bill.recipe.ingredients.Sum(i => (int)i.CountRequiredOfFor(resource, bill.recipe, bill))) return false;
        job = JobMaker.MakeJob(JobDefOf.DoBill, patient); job.bill = bill;
        job.targetQueueB = inputs.Select(i => new LocalTargetInfo(i.Thing)).ToList(); job.countQueue = inputs.Select(i => i.Count).ToList();
        job.haulMode = HaulMode.ToCellNonStorage; job.workGiverDef = giver.def;
        return true;
    }
}

