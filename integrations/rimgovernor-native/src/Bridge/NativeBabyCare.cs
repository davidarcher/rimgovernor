using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native;

/// <summary>Read-only childcare admissions and native service geometry. Never creates a job or samples a random destination.</summary>
internal static class NativeBabyCare
{
    /// <summary>Actual native care occupation, including its finalizer. This is read again at order admission
    /// so an order prepared before care started cannot interrupt it.</summary>
    internal static bool Occupied(Pawn pawn) => pawn.jobs?.curDriver is JobDriver_Breastfeed or JobDriver_BottleFeedBaby or JobDriver_BringBabyToSafety
        || pawn.carryTracker?.CarriedThing is Pawn baby && baby.DevelopmentalStage.Baby()
        || pawn.MapHeld?.mapPawns.FreeColonistsSpawned.Any(carer => carer.IsCarryingPawn(pawn)
            || carer.CurJob?.targetA.Thing == pawn && carer.jobs?.curDriver is JobDriver_Breastfeed or JobDriver_BottleFeedBaby) == true;

    internal static BabyCareRead Read(Pawn baby, Map map, BabyPlayRead? play)
    {
        // A walking carer can chain random wander legs; Near placement can widen its search.
        // All region cells deliberately enclose those choices rather than assigning a random destination law.
        var roaming = new ServicePositionRead.PassableMap();
        var options = new List<BabyCareOpportunityRead>();
        BabyCareActiveRead? active = null;
        foreach (var carer in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var driver = carer.jobs?.curDriver;
            var kind = Kind(driver);
            if (carer.IsCarryingPawn(baby) || carer.CurJob?.targetA.Thing == baby && kind != null)
            {
                if (active != null) throw new RequiredFactException("multiple active baby carers:" + baby.GetUniqueLoadID());
                active = new(carer.GetUniqueLoadID(), kind ?? BabyCareKind.Carry, At(baby.Position), At(carer.Position), roaming, Mode(driver));
            }
            if (!ChildcareUtility.CanHaulBaby(carer, baby, out _)
                || !(carer.IsCarryingPawn(baby) || carer.CanReserveAndReach(baby, PathEndMode.Touch, Danger.Some))) continue;
            var childcareWorker = !carer.DevelopmentalStage.Juvenile() && !carer.WorkTypeIsDisabled(WorkTypeDefOf.Childcare)
                && carer.workSettings != null && carer.workSettings.WorkIsActive(WorkTypeDefOf.Childcare);

            if (childcareWorker && play?.Carers.Any(c => c.Pawn == carer.GetUniqueLoadID()) == true)
            {
                foreach (var activity in DefDatabase<BabyPlayDef>.AllDefsListForReading)
                {
                    var playKind = activity.Worker switch
                    {
                        BabyPlayGiver_PlayStatic => BabyCareKind.StaticPlay,
                        BabyPlayGiver_PlayWalking => BabyCareKind.WalkingPlay,
                        BabyPlayGiver_PlayToys => BabyCareKind.ToyPlay,
                        _ => throw new RequiredFactException("baby care play giver:" + activity.defName),
                    };
                    // Toy availability for each scenario remains owned by BabyPlayCarerRead and projected sites.
                    var position = playKind == BabyCareKind.StaticPlay ? new ServicePositionRead.Located(At(baby.Position)) : (ServicePositionRead)roaming;
                    // Installed drivers and their base setup toils retain Toil's Normal mode and have no DesiredSocialMode override.
                    options.Add(new(carer.GetUniqueLoadID(), playKind, 0, position, roaming, ActivitySocialMode.Normal));
                }
            }
            if (baby.needs?.food is not { } food || !ChildcareUtility.CanFeedBaby(carer, baby, out _)) continue;
            var charge = carer.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Lactating)?.TryGetComp<HediffComp_Chargeable>();
            if (ChildcareUtility.CanAutoBreastfeed(carer, baby, false, out _) && charge != null)
            {
                // Zero ticksToFullCharge fills instantly on its next native comp tick. A full charge per tick is a finite upper bound.
                var refill = charge.Props.ticksToFullCharge < 0 ? 0 : charge.Props.fullChargeAmount / Math.Max(1, charge.Props.ticksToFullCharge) / food.MaxLevel;
                if (charge.CanActivate || refill > 0)
                    options.Add(new(carer.GetUniqueLoadID(), BabyCareKind.Breastfeed, Nonnegative(charge.Charge / food.MaxLevel), roaming, roaming, ActivitySocialMode.Normal)
                        { FoodRefillPerTick = Nonnegative(refill), ReadyToFeed = charge.CanActivate });
            }
            // FindBabyFoodForBaby is the workgiver's non-mutating food search, with native food policies and reservations.
            // The manual bottle workgiver does not gate its feeder on this baby's breastfeeding assignment.
            // Urgent autofeeding also remains native when ordinary Childcare work is inactive.
            if ((childcareWorker || baby.mindState.AutofeedSetting(carer) == AutofeedMode.Urgent)
                && ChildcareUtility.FindBabyFoodForBaby(carer, baby) is { } source)
                options.Add(new(carer.GetUniqueLoadID(), BabyCareKind.BottleFeed,
                    Nonnegative(FoodUtility.NutritionForEater(baby, source) * source.stackCount / food.MaxLevel), roaming, roaming, ActivitySocialMode.Normal)
                    { ReadyToFeed = true });
        }
        return new()
        {
            Active = active,
            // Both feeding drivers add MaxLevel/5000 per service tick; there is no public native rate symbol.
            // Hunger categories can change before a later bout, so enclose their native fall rates, rather than freeze starvation's rate.
            Food = baby.needs?.food is { } need ? new(Nonnegative(need.CurLevelPercentage),
                Enum.GetValues(typeof(HungerCategory)).Cast<HungerCategory>().Max(c => Nonnegative(need.FoodFallPerTickAssumingCategory(c) / need.MaxLevel)), 1d / 5000)
                { WantsFeeding = ChildcareUtility.WantsSuckle(baby, out _) } : null,
            Play = baby.needs?.play is { } needPlay ? new BabyCarePlayRead(Nonnegative(needPlay.CurLevelPercentage)) : null,
            Opportunities = EquatableArray.From(options),
        };
    }

    private static BabyCareKind? Kind(JobDriver? driver) => driver switch
    {
        JobDriver_Breastfeed => BabyCareKind.Breastfeed,
        JobDriver_BottleFeedBaby => BabyCareKind.BottleFeed,
        JobDriver_PlayStatic => BabyCareKind.StaticPlay,
        JobDriver_PlayWalking => BabyCareKind.WalkingPlay,
        JobDriver_PlayToys => BabyCareKind.ToyPlay,
        _ => null,
    };

    // DesiredSocialMode is a public native observation, including overrides and current toil mode.
    private static ActivitySocialMode Mode(JobDriver? driver) => (driver?.DesiredSocialMode() ?? RandomSocialMode.Off) switch
    {
        RandomSocialMode.Off => ActivitySocialMode.Off, RandomSocialMode.Quiet => ActivitySocialMode.Quiet,
        RandomSocialMode.Normal => ActivitySocialMode.Normal, RandomSocialMode.SuperActive => ActivitySocialMode.SuperActive,
        var mode => throw new RequiredFactException("baby care social mode:" + mode),
    };
    private static Cell At(IntVec3 cell) => new(cell.x, cell.z);
    private static double Nonnegative(double value) => double.IsFinite(value) && value >= 0 ? value
        : throw new RequiredFactException("baby care finite nonnegative value");
}
