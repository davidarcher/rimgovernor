package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Eat trigger. Vanilla colonists seek food at 30% of the bar, so a
// 0.9-nutrition meal overfills the bar by about 0.2 and the overflow is
// wasted. Eating at 12.5% wastes almost none of it and stays out of the
// Ravenous mood (below 12.5%), which stretches a stock by about a quarter
// (spike #2768: 57 survival meals last 24.9 days at 30% and 31.2 at 12.5%).
// Hitting 0 asleep is survivable (about 52 h to death), so there is no sleep
// floor. The runway is the guard: below the foothold emergency line the
// colony eats at the vanilla trigger; a solo colonist or a runway below the
// seasonal minimum takes the lowered one. An unknown runway writes nothing.
// Native reports the effective trigger and clears its override at 0.30 or
// more (#2794).

const (
	// EatTriggerVanilla is RimWorld's food-seeking threshold.
	EatTriggerVanilla = 0.30
	// EatTriggerLowered is the trigger for a solo or low-runway colony.
	EatTriggerLowered = 0.125

	// Hunger arithmetic of the spike: an adult needs 1.6 nutrition a day and
	// a survival or simple meal restores 0.9.
	nutritionPerDay  = 1.6
	nutritionPerMeal = 0.9
)

// EatTriggerPawn is one colonist's eat trigger inputs. Current is unknown
// where the read did not carry it (never written); Guest is a guest or
// prisoner, whom native refuses; Read is true when the row carried its
// policy inputs, without which a guest cannot be told from a colonist.
type EatTriggerPawn struct {
	ID      PawnID
	Current domain.Fact[float64]
	Guest   bool
	Read    bool
}

// EatTriggerColony is the colony-wide input: the accessible food runway in
// days, the colonist count, the foothold emergency line and the seasonal
// minimum runway.
type EatTriggerColony struct {
	FoodDays      domain.Fact[float64]
	Colonists     domain.Fact[int64]
	EmergencyDays float64
	MinDays       float64
}

// EatTriggerPawn lifts a work census row into its eat trigger inputs.
func (w WorkPawn) EatTriggerPawn() EatTriggerPawn {
	p := EatTriggerPawn{ID: w.ID, Current: w.EatTrigger}
	if in, ok := w.PolicyInputs.Value(); ok {
		p.Read = true
		p.Guest = in.GuestStatus == "Guest" || in.GuestStatus == "Prisoner"
	}
	return p
}

// WantEatTrigger is the trigger every colonist should hold; false when the
// runway is unknown or not a number.
func WantEatTrigger(c EatTriggerColony) (float64, bool) {
	days, ok := c.FoodDays.Value()
	if !ok || math.IsNaN(days) {
		return 0, false
	}
	if days < c.EmergencyDays {
		return EatTriggerVanilla, true
	}
	if n, known := c.Colonists.Value(); known && n == 1 || days < c.MinDays {
		return EatTriggerLowered, true
	}
	return EatTriggerVanilla, true
}

// EatTriggerChanges are the settings to write: every colonist with a known
// trigger that differs from the wanted one.
func EatTriggerChanges(pawns []EatTriggerPawn, c EatTriggerColony) []domain.PawnSettings {
	want, ok := WantEatTrigger(c)
	if !ok {
		return nil
	}
	var out []domain.PawnSettings
	for _, p := range pawns {
		current, ck := p.Current.Value()
		// The wire carries a float32; compare at its precision.
		if !ck || !p.Read || p.Guest || math.Abs(current-want) < 1e-6 {
			continue
		}
		if s, err := domain.NewEatTriggerSetting(domain.PawnID(p.ID), want); err == nil {
			out = append(out, s)
		}
	}
	return out
}

// EatTriggerOwed is the review's EatTriggerOwed fact: a change is owed.
func EatTriggerOwed(pawns domain.Fact[[]WorkPawn], c EatTriggerColony) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(EatTriggerChanges(EatTriggerRows(rows), c)) > 0)
}

// EatTriggerRows lifts the work census into eat trigger rows.
func EatTriggerRows(pawns []WorkPawn) []EatTriggerPawn {
	out := make([]EatTriggerPawn, 0, len(pawns))
	for _, w := range pawns {
		out = append(out, w.EatTriggerPawn())
	}
	return out
}

// EatTriggerDaysSaved is the days a food runway gains when colonists eat at
// level instead of the vanilla trigger; runwayDays is the runway at the
// vanilla trigger. A meal restores at most the room under the trigger, so
// the colony burns 1.6 x 0.9 / min(0.9, 1-level) nutrition a day and the
// runway scales by the ratio of the two rates. A level at or above vanilla
// saves nothing.
func EatTriggerDaysSaved(runwayDays, level float64) float64 {
	if level >= EatTriggerVanilla || level < 0 || math.IsNaN(level) || runwayDays <= 0 || math.IsNaN(runwayDays) {
		return 0
	}
	return runwayDays * (mealFill(level)/mealFill(EatTriggerVanilla) - 1)
}

// MealRunwayDays is the days a solo colonist's meals last at the vanilla
// trigger.
func MealRunwayDays(meals float64) float64 {
	return meals * nutritionPerMeal / (nutritionPerDay * nutritionPerMeal / mealFill(EatTriggerVanilla))
}

// mealFill is the nutrition one meal restores when eaten at level.
func mealFill(level float64) float64 { return math.Min(nutritionPerMeal, 1-level) }
