package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// YearDays is the native 60-day year every calendar fact is bounded by.
const YearDays = 60

// Calendar is the map tile's native growing calendar, read with the other
// routine facts. GrowingDays is the tile's growing period per year;
// GrowingDaysRemaining the days until the seasonal temperature next leaves
// the crop growth range (YearDays while it never does); GrowingDaysUntil the
// days until it next re-enters it (0 while crops grow now, YearDays when the
// tile never grows). Sowing is the native growth-season flag for the starter
// crops. Season and DayOfYear are informational.
type Calendar struct {
	Season                                              string
	DayOfYear                                           int64
	GrowingDays, GrowingDaysRemaining, GrowingDaysUntil float64
	Sowing                                              bool
}

// Valid reports whether every day count is a finite value inside one year
// and the flags agree: crops grow now exactly when there is no wait.
func (c Calendar) Valid() bool {
	for _, days := range []float64{c.GrowingDays, c.GrowingDaysRemaining, c.GrowingDaysUntil} {
		if math.IsNaN(days) || math.IsInf(days, 0) || days < 0 || days > YearDays {
			return false
		}
	}
	return c.DayOfYear >= 0 && c.DayOfYear < YearDays
}

// HarvestGapDays is the stretch without a field harvest the colony must hold
// stored food against right now. While crops do not grow it is the wait
// until growth resumes; while they do it is the coming non-growing part of
// the year (zero on a tile that grows all year), phased in linearly over the
// gap plus one field cycle before the frost so the larder fills as the
// frost nears instead of the whole summer reading as a deficit. Either way
// one grow cycle of the fastest starter crop is added, because a harvest
// lags the first growing day, and the whole is capped at one year. Unknown
// while the calendar is unknown or invalid.
//
// An observed growth pause (GrowthPauseDays: a volcanic winter or cold snap
// with a remaining-duration read) extends the gap. While crops grow it is
// added outright, since the next harvest waits for the pause to lift; while
// they do not, the pause plus the first harvest cycle stands in for a
// shorter seasonal wait. A pause on an unknown calendar is the whole gap.
func HarvestGapDays(calendar domain.Fact[Calendar], conditions domain.Fact[[]DisasterCondition]) domain.Fact[float64] {
	pause := GrowthPauseDays(conditions)
	c, known := calendar.Value()
	if !known || !c.Valid() {
		if pause <= 0 {
			return domain.Unknown[float64]()
		}
		return domain.Known(math.Min(YearDays, pause+firstHarvestDays))
	}
	if c.GrowingDaysUntil > 0 {
		return domain.Known(math.Min(YearDays, math.Max(c.GrowingDaysUntil, pause)+firstHarvestDays))
	}
	winter := YearDays - c.GrowingDays
	var gap float64
	if winter > 0 {
		gap = math.Min(YearDays, winter+firstHarvestDays)
		lead := gap + fieldCycles*firstHarvestDays
		gap *= math.Max(0, math.Min(1, 1-c.GrowingDaysRemaining/lead))
	}
	return domain.Known(math.Min(YearDays, gap+pause))
}

// firstHarvestDays is the grow cycle the harvest gap adds past the first
// growing day: the fastest starter crop (rice) at its native growth rate on
// ordinary soil. fieldCycles is the field budget's cycles per harvest
// (FieldCoverage), the lead a sown field needs before the frost.
const (
	firstHarvestDays = 3
	fieldCycles      = 2.5
)

// Seasonal returns the policy with its stored-food and wood thresholds
// widened by the harvest gap the calendar reports: FoodMinDays and
// FoodTargetDays both grow by the gap, and WoodMin, WoodTarget and WoodMax
// by the target's factor, so a colony fills its fields, larder and woodpile
// against the coming winter instead of holding the flat summer thresholds
// into the first frost. FootholdFoodDays and every other field are
// unchanged, an unknown calendar with no observed growth pause leaves the
// policy as configured, and the result still satisfies Validate.
func (p RoutinePolicy) Seasonal(calendar domain.Fact[Calendar], conditions domain.Fact[[]DisasterCondition]) RoutinePolicy {
	gap, known := HarvestGapDays(calendar, conditions).Value()
	if !known || gap <= 0 || p.FoodTargetDays <= 0 {
		return p
	}
	target := math.Min(YearDays, p.FoodTargetDays+gap)
	if target <= p.FoodTargetDays {
		return p
	}
	scale := func(n int64) int64 { return max(n, int64(math.Ceil(float64(n)*target/p.FoodTargetDays))) }
	p.WoodMin, p.WoodTarget, p.WoodMax = scale(p.WoodMin), scale(p.WoodTarget), scale(p.WoodMax)
	p.FoodMinDays = math.Min(p.FoodMinDays+gap, target-(p.FoodTargetDays-p.FoodMinDays))
	p.FoodTargetDays = target
	return p
}
