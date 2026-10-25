using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>Conditional native childcare occupancy bounds. Native Childcare owns the jobs; these rows allocate nobody.</summary>
internal static class ForecastBabyCare
{
    /// <summary>Shares bound the total time budget within Start..End, not pointwise occupancy probabilities.
    /// Future service may occur anywhere in that interval; only the one-tick active snapshot is pointwise exact.</summary>
    internal sealed record Presence(string Baby, string Carer, double Start, double End, double LowerShare, double UpperShare,
        ServicePositionRead BabyPosition, ServicePositionRead CarerPosition, ForecastSocialService Social)
    {
        /// <summary>Alternatives with the same demand share one need deficit, never independent time budgets.</summary>
        public required string Demand { get; init; }
    }

    /// <summary>Conservation of need: gross recovery can supply at most the initial deficit plus horizon decay.
    /// This bounds the total service time of every possible job sequence, without averaging random choices.</summary>
    internal static double ServiceShare(double level, double fall, double gain, double horizon)
    {
        if (!double.IsFinite(level) || level < 0 || level > 1 || !double.IsFinite(fall) || fall < 0
            || !double.IsFinite(gain) || gain <= fall || !double.IsFinite(horizon) || horizon <= 0)
            throw new RequiredFactException("baby care service rates");
        return Math.Clamp((1 - level + fall * horizon) / gain / horizon, 0, 1);
    }

    internal static IReadOnlyList<Presence> Of(ForecastCoupledContext context, bool withNursery, double horizon, ISet<string> findings)
    {
        if (!double.IsFinite(horizon) || horizon <= 0) throw new RequiredFactException("baby care horizon");
        var result = new List<Presence>();
        var room = context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Nursery).Select(r => r.Key).DefaultIfEmpty(0).Min();
        var index = 0;
        foreach (var baby in context.Needs.Babies.Babies.OrderBy(b => b.Pawn, StringComparer.Ordinal))
        {
            var care = baby.Care;
            if (care.Active is { } active)
            {
                result.Add(new(baby.Pawn, active.Carer, 0, Math.Min(1, horizon), 1, 1,
                    new ServicePositionRead.Located(active.BabyPosition), new ServicePositionRead.Located(active.CarerPosition),
                    new ForecastSocialService.Verified(active.Social)) { Demand = "active/" + baby.Pawn });
                findings.Add("nursery_care_active_snapshot:" + baby.Pawn + ":" + active.Carer);
                if (horizon > 1)
                {
                    var (demand, share) = ActiveShare(baby, active, context, index, room, horizon);
                    result.Add(new(baby.Pawn, active.Carer, 1, horizon, 0, share,
                        active.FuturePositions,
                        active.FuturePositions, active.Kind == BabyCareKind.Carry ? new ForecastSocialService.Unprojected()
                            : new ForecastSocialService.Verified(ActivitySocialMode.Normal)) { Demand = demand });
                    findings.Add(active.Kind == BabyCareKind.Carry ? "nursery_care_active_carry_drop_time_unprojected:" + baby.Pawn
                        : "nursery_care_active_remaining_service_bounded:" + baby.Pawn);
                }
            }
            foreach (var option in care.Opportunities)
            {
                double upper;
                string demand;
                var babyPosition = option.BabyPosition;
                var carerPosition = option.CarerPosition;
                if (option.Kind is BabyCareKind.Breastfeed or BabyCareKind.BottleFeed)
                {
                    var food = care.Food ?? throw new RequiredFactException("baby care food:" + baby.Pawn);
                    if (!double.IsFinite(option.FoodAvailable) || option.FoodAvailable < 0 || !double.IsFinite(option.FoodRefillPerTick)
                        || option.FoodRefillPerTick < 0) throw new RequiredFactException("baby care nutrition");
                    var supply = option.FoodAvailable + option.FoodRefillPerTick * horizon;
                    if (supply == 0) continue;
                    upper = ServiceShare(food.Level, food.FallPerTick, food.GainPerTick, horizon);
                    upper = Math.Min(upper, supply / food.GainPerTick / horizon);
                    demand = "food/" + baby.Pawn;
                    findings.Add("nursery_care_feeding_native_charge_or_food:" + baby.Pawn);
                }
                else if (option.Kind is BabyCareKind.StaticPlay or BabyCareKind.WalkingPlay or BabyCareKind.ToyPlay)
                {
                    var play = baby.Play ?? throw new RequiredFactException("baby care play:" + baby.Pawn);
                    var carer = play.Carers.SingleOrDefault(c => c.Pawn == option.Carer)
                        ?? throw new RequiredFactException("baby care play carer:" + option.Carer);
                    if (option.Kind == BabyCareKind.ToyPlay && (withNursery ? carer.Toy : baby.InNursery ? carer.ToyWithoutNursery : carer.Toy) == null
                        && !(withNursery && context.NurserySites.Any(s => context.Needs.Babies.Play!.Buildings.Any(b => !b.Decoration && b.Def == s.Definition)))) continue;
                    var gains = ForecastBabyPlay.GainBounds(baby, index, room, context, carer);
                    upper = ServiceShare(care.Play?.Level ?? throw new RequiredFactException("baby care play level"),
                        context.Needs.Babies.Play!.FallPerTick, withNursery ? gains.WithMin : gains.WithoutMin, horizon);
                    demand = "play/" + baby.Pawn;
                    if (option.Kind == BabyCareKind.StaticPlay)
                    {
                        babyPosition = ForecastNurseryExposure.RestSource(context, baby, withNursery);
                        carerPosition = Touch(babyPosition, context);
                    }
                }
                else throw new RequiredFactException("baby care opportunity kind:" + option.Kind);
                if (upper == 0) continue;
                result.Add(new(baby.Pawn, option.Carer, 0, horizon, 0, upper, babyPosition, carerPosition,
                    new ForecastSocialService.Verified(option.Social)) { Demand = demand });
                findings.Add("nursery_care_native_service_time_bounded:" + baby.Pawn);
                findings.Add("nursery_care_unassigned_carer:" + baby.Pawn);
                findings.Add("nursery_care_interruptions_and_random_locations_bounded:" + baby.Pawn);
            }
            index++;
        }
        return result;
    }

    private static (string Demand, double Share) ActiveShare(BabyRead baby, BabyCareActiveRead active,
        ForecastCoupledContext context, int index, int room, double horizon)
    {
        // A pure haul has no need-recovery budget. Its observed current pickup source persists until a native drop;
        // absent a completed drop, retain the entire remaining horizon as a conservative possibility.
        if (active.Kind == BabyCareKind.Carry) return ("carry/" + baby.Pawn, 1);
        if (active.Kind is BabyCareKind.Breastfeed or BabyCareKind.BottleFeed)
        {
            var food = baby.Care.Food ?? throw new RequiredFactException("active baby feeding need");
            return ("food/" + baby.Pawn, ServiceShare(food.Level, food.FallPerTick, food.GainPerTick, horizon));
        }
        var play = baby.Play ?? throw new RequiredFactException("active baby play need");
        var carer = play.Carers.SingleOrDefault(c => c.Pawn == active.Carer) ?? throw new RequiredFactException("active baby play carer");
        var gains = ForecastBabyPlay.GainBounds(baby, index, room, context, carer);
        // The active job predates either room counterfactual; enclose both gain scenarios for its continuation.
        return ("play/" + baby.Pawn, ServiceShare(baby.Care.Play?.Level ?? throw new RequiredFactException("active baby play level"),
            context.Needs.Babies.Play!.FallPerTick, Math.Min(gains.WithMin, gains.WithoutMin), horizon));
    }

    private static ServicePositionRead Touch(ServicePositionRead source, ForecastCoupledContext context)
    {
        var cells = source switch
        {
            ServicePositionRead.Located located => new[] { located.Cell },
            ServicePositionRead.Possible possible => possible.Cells.ToArray(),
            ServicePositionRead.Unprojected or ServicePositionRead.PassableMap => null,
            var invalid => throw new ArgumentOutOfRangeException(nameof(source), invalid, "Invalid baby care source."),
        };
        if (cells == null) return source;
        var geometry = context.Needs.Babies.Geometry ?? throw new RequiredFactException("baby care map dimensions");
        // ClosestTouch includes the target and all eight neighbours; future legality can only narrow this enclosure.
        return new ServicePositionRead.Possible(EquatableArray.From(cells.SelectMany(c =>
            from x in Enumerable.Range(-1, 3) from z in Enumerable.Range(-1, 3) select new Cell(c.X + x, c.Z + z))
            .Where(c => c.X >= 0 && c.X < geometry.Width && c.Z >= 0 && c.Z < geometry.Height).Distinct()));
    }
}
