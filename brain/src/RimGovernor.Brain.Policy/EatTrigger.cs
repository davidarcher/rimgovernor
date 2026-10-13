namespace RimGovernor.Brain.Policy;

/// <summary>
/// One colonist's eat trigger inputs. Current is unknown where the read did not carry it (never written); Guest
/// is a guest or prisoner, whom native refuses; Read is true when the row carried its policy inputs, without
/// which a guest cannot be told from a colonist.
/// </summary>
public sealed record EatTriggerPawn(PawnId Id, Fact<double> Current, bool Guest, bool Read);

/// <summary>The colony-wide input: the accessible food runway in days, the colonist count, the foothold emergency line and the seasonal minimum runway.</summary>
public sealed record EatTriggerColony(Fact<double> FoodDays, Fact<long> Colonists, double EmergencyDays, double MinDays);

// Eat trigger. Vanilla colonists seek food at 30% of the bar, so a 0.9-nutrition meal overfills the bar by about
// 0.2 and the overflow is wasted. Eating at 12.5% wastes almost none of it and stays out of the Ravenous mood,
// which stretches a stock by about a quarter. The runway is the guard: below the foothold emergency line the
// colony eats at the vanilla trigger; a solo colonist or a runway below the seasonal minimum takes the lowered
// one. An unknown runway writes nothing.
public static partial class PolicyFunctions
{
    /// <summary>RimWorld's food-seeking threshold.</summary>
    public const double EatTriggerVanilla = 0.30;

    /// <summary>The trigger for a solo or low-runway colony.</summary>
    public const double EatTriggerLowered = 0.125;

    // Hunger arithmetic of the spike: an adult needs 1.6 nutrition a day and a survival or simple meal restores 0.9.
    private const double NutritionPerDay = 1.6;
    private const double NutritionPerMeal = 0.9;

    /// <summary>Lifts a work census row into its eat trigger inputs.</summary>
    public static EatTriggerPawn ToEatTriggerPawn(this WorkPawn w)
    {
        var read = false;
        var guest = false;
        if (w.PolicyInputs.TryGetValue(out var input))
        {
            read = true;
            guest = input.GuestStatus is "Guest" or "Prisoner";
        }
        return new EatTriggerPawn(w.Id, w.EatTrigger, guest, read);
    }

    /// <summary>The trigger every colonist should hold; null when the runway is unknown or not a number.</summary>
    public static double? WantEatTrigger(EatTriggerColony c)
    {
        if (!c.FoodDays.TryGetValue(out var days) || double.IsNaN(days))
        {
            return null;
        }
        if (days < c.EmergencyDays)
        {
            return EatTriggerVanilla;
        }
        if ((c.Colonists.TryGetValue(out var n) && n == 1) || days < c.MinDays)
        {
            return EatTriggerLowered;
        }
        return EatTriggerVanilla;
    }

    /// <summary>The settings to write: every colonist with a known trigger that differs from the wanted one.</summary>
    public static IReadOnlyList<PawnSettings> EatTriggerChanges(IEnumerable<EatTriggerPawn> pawns, EatTriggerColony c)
    {
        var output = new List<PawnSettings>();
        if (WantEatTrigger(c) is not { } want)
        {
            return output;
        }
        foreach (var p in pawns)
        {
            // The wire carries a float32; compare at its precision.
            if (!p.Current.TryGetValue(out var current) || !p.Read || p.Guest || Math.Abs(current - want) < 1e-6)
            {
                continue;
            }
            try
            {
                output.Add(PawnSettings.NewEatTriggerSetting(p.Id, want));
            }
            catch (DomainException)
            {
            }
        }
        return output;
    }

    /// <summary>The review's EatTriggerOwed fact: a change is owed.</summary>
    public static Fact<bool> EatTriggerOwed(Fact<EquatableArray<WorkPawn>> pawns, EatTriggerColony c)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(EatTriggerChanges(EatTriggerRows(rows), c).Count > 0);
    }

    /// <summary>Lifts the work census into eat trigger rows.</summary>
    public static List<EatTriggerPawn> EatTriggerRows(IEnumerable<WorkPawn> pawns) => pawns.Select(w => w.ToEatTriggerPawn()).ToList();

    /// <summary>
    /// The days a food runway gains when colonists eat at level instead of the vanilla trigger; runwayDays is the
    /// runway at the vanilla trigger. A meal restores at most the room under the trigger, so the colony burns
    /// 1.6 x 0.9 / min(0.9, 1-level) nutrition a day and the runway scales by the ratio of the two rates. A level
    /// at or above vanilla saves nothing.
    /// </summary>
    public static double EatTriggerDaysSaved(double runwayDays, double level)
    {
        if (level >= EatTriggerVanilla || level < 0 || double.IsNaN(level) || runwayDays <= 0 || double.IsNaN(runwayDays))
        {
            return 0;
        }
        return runwayDays * (MealFill(level) / MealFill(EatTriggerVanilla) - 1);
    }

    /// <summary>The days a solo colonist's meals last at the vanilla trigger.</summary>
    public static double MealRunwayDays(double meals) =>
        meals * NutritionPerMeal / (NutritionPerDay * NutritionPerMeal / MealFill(EatTriggerVanilla));

    /// <summary>The nutrition one meal restores when eaten at level.</summary>
    private static double MealFill(double level) => Math.Min(NutritionPerMeal, 1 - level);
}
