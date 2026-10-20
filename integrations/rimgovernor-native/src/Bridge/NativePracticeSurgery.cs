#nullable enable
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
namespace HomeBridge.BridgeTools;

/// <summary>
/// A selected practice surgery (#3637): the step's exact recipe on the step's part of a colony prisoner, run as the doctor's native
/// DoBill on a bill this step queues through <see cref="HealthCardUtility.CreateSurgeryBill"/>, restricted to the doctor
/// (<see cref="Bill.SetPawnRestriction"/>) and with its ingredient filter narrowed to the step's medicine kind, which the native
/// medicine search (WorkGiver_DoBill's IsFixedOrAllowedIngredient) then takes. Any other medical bill on the patient refuses it.
/// </summary>
internal static class NativePracticeSurgery
{
    private static bool Resolve(Pawn patient, PawnActivitySurgery surgery, out RecipeDef? recipe, out BodyPartRecord? part, out ThingDef? medicine)
    {
        recipe = patient.def.AllRecipes?.FirstOrDefault(r => r.defName == surgery.Recipe);
        var parts = patient.RaceProps.body.AllParts;
        part = surgery.Part >= 0 && surgery.Part < parts.Count ? parts[surgery.Part] : null;
        medicine = DefDatabase<ThingDef>.GetNamedSilentFail(surgery.Medicine);
        return recipe != null && part != null && medicine is { IsMedicine: true };
    }

    /// <summary>A fresh read still supports the step: a spawned living colony prisoner the recipe applies to on that part now, whose
    /// care allows the medicine, with no medical bill but this step's own.</summary>
    internal static bool Supported(Map map, PawnActivityStep step)
    {
        if (step.Practice?.Surgery is not { } surgery || map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.GetUniqueLoadID() == step.Target) is not { } patient
            || patient.Dead || !Resolve(patient, surgery, out var recipe, out var part, out var medicine)) return false;
        return recipe!.AvailableNow && recipe.Worker.AvailableReport(patient).Accepted && recipe.AvailableOnNow(patient, part)
            && recipe.Worker.GetPartsToApplyOn(patient, recipe).Contains(part) && recipe.ingredients.Any(i => i.filter.Allows(medicine!))
            && patient.playerSettings?.medCare.AllowsMedicine(medicine!) == true
            && patient.BillStack.Bills.OfType<Bill_Medical>().All(b => b.recipe == recipe && b.Part == part);
    }

    internal static bool Legal(Pawn doctor, Thing target, PawnActivityStep step) =>
        target is Pawn patient && Supported(doctor.Map, step) && Resolve(patient, step.Practice!.Surgery!, out var recipe, out _, out _)
        && !doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && recipe!.PawnSatisfiesSkillRequirements(doctor)
        && doctor.CanReach(patient, PathEndMode.Touch, Danger.Deadly);

    /// <summary>This step's bill on the patient: the recipe on the part, restricted to the doctor.</summary>
    private static Bill_Medical? Ours(Pawn patient, Pawn doctor, PawnActivitySurgery surgery) =>
        Resolve(patient, surgery, out var recipe, out var part, out _)
            ? patient.BillStack.Bills.OfType<Bill_Medical>().FirstOrDefault(b => b.recipe == recipe && b.Part == part && b.PawnRestriction == doctor) : null;

    /// <summary>The doctor's native DoBill on this step's bill, queuing it first; null while the patient is not in bed (native
    /// Pawn.CurrentlyUsableForBills) or native DoBill offers no job on it yet, so the step holds.</summary>
    internal static Job? Job(Pawn doctor, Pawn patient, PawnActivityStep step)
    {
        var surgery = step.Practice!.Surgery!;
        var bill = Ours(patient, doctor, surgery);
        if (bill == null)
        {
            Resolve(patient, surgery, out var recipe, out var part, out var medicine);
            bill = HealthCardUtility.CreateSurgeryBill(patient, recipe!, part, sendMessages: false);
            bill.SetPawnRestriction(doctor);
            foreach (var other in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsMedicine && d != medicine)) bill.ingredientFilter.SetAllow(other, false);
            bill.ingredientFilter.SetAllow(medicine!, true);
        }
        if (!patient.InBed()) return null;
        var giver = DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver);
        var job = ((WorkGiver_DoBill)giver.Worker).JobOnThing(doctor, patient, false);
        if (job == null) return null;
        if (job.bill != bill || job.def != JobDefOf.DoBill) { JobMaker.ReturnToPool(job); return null; }
        job.workGiverDef = giver;
        return job;
    }

    /// <summary>The step's prisoner lies in bed, so native DoBill can start on it (Pawn.CurrentlyUsableForBills).</summary>
    internal static bool PatientReady(Map map, PawnActivityStep step) =>
        map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == step.Target) is { } patient && patient.InBed();

    /// <summary>Deletes the step's still-queued bill (#3637): a released, replaced or failed practice surgery leaves no bill behind.</summary>
    internal static void Cancel(Pawn doctor, PawnActivityStep step)
    {
        if (step.Practice?.Surgery is not { } surgery || doctor.MapHeld is not { } map
            || map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == step.Target) is not { } patient) return;
        if (Ours(patient, doctor, surgery) is { } bill) patient.BillStack.Delete(bill);
    }
}
