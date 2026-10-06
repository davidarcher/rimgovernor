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
// tile never grows); NonGrowingDays the length of the current or coming
// non-growing stretch on the same walk (0 while the tile never leaves the
// range, equal to GrowingDaysUntil while crops do not grow). Sowing is the
// native growth-season flag for the starter crops. GrowingDays, Season and
// DayOfYear are informational.
type Calendar struct {
	Season                                                              string
	DayOfYear                                                           int64
	GrowingDays, GrowingDaysRemaining, GrowingDaysUntil, NonGrowingDays float64
	Sowing                                                              bool
}

// Valid reports whether every day count is a finite value inside one year
// and the flags agree: crops grow now exactly when there is no wait.
func (c Calendar) Valid() bool {
	for _, days := range []float64{c.GrowingDays, c.GrowingDaysRemaining, c.GrowingDaysUntil, c.NonGrowingDays} {
		if math.IsNaN(days) || math.IsInf(days, 0) || days < 0 || days > YearDays {
			return false
		}
	}
	return c.DayOfYear >= 0 && c.DayOfYear < YearDays
}

// HarvestGapDays is the stretch without a field harvest the colony must hold
// stored food against right now. While crops do not grow it is the wait
// until growth resumes; while they do it is the coming non-growing stretch
// the same daily walk reports (zero on a tile that grows all year), phased
// in linearly over the gap plus one field cycle before the frost so the
// larder fills as the frost nears instead of the whole summer reading as a
// deficit. The phase-in is complete on the last growing day (one day
// remaining), so the thresholds the frost brings are the ones the colony
// already held (#317). Either way one grow cycle of the fastest starter
// crop is added, because a harvest lags the first growing day, and the
// whole is capped at one year. Unknown while the calendar is unknown or
// invalid.
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
	var gap float64
	if c.NonGrowingDays > 0 {
		gap = math.Min(YearDays, c.NonGrowingDays+firstHarvestDays)
		lead := gap + fieldCycles*firstHarvestDays
		gap *= math.Max(0, math.Min(1, 1-(c.GrowingDaysRemaining-1)/lead))
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
func (p RoundsPolicy) Seasonal(calendar domain.Fact[Calendar], conditions domain.Fact[[]DisasterCondition]) RoundsPolicy {
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

// CropGrowth is the raw state of one growing zone's crop as native mirrors
// it: the planted plants' mean growth fraction and fertility factor, the
// outdoor temperature and the crop's native growth range. Light is not part
// of it: the factor is an instantaneous reading that drops to zero every
// night, so it says nothing about days to harvest.
type CropGrowth struct {
	Planted, FertilePlanted            domain.Fact[int64]
	GrowthMean, FertilityMean          domain.Fact[float64]
	Temperature, MinGrowth, MinOptimal domain.Fact[float64]
	MaxOptimal, MaxGrowth              domain.Fact[float64]
}

// TemperatureFactor is the crop's growth-rate factor at the current outdoor
// temperature: 1 inside the optimal range, falling linearly to 0 at the
// growth limits. Unknown while a temperature fact is missing or the range is
// not ordered.
func (g CropGrowth) TemperatureFactor() domain.Fact[float64] {
	t, tk := g.Temperature.Value()
	lo, lk := g.MinGrowth.Value()
	lo2, l2k := g.MinOptimal.Value()
	hi2, h2k := g.MaxOptimal.Value()
	hi, hk := g.MaxGrowth.Value()
	if !tk || !lk || !l2k || !h2k || !hk || !(lo <= lo2 && lo2 <= hi2 && hi2 <= hi) || math.IsNaN(t) || math.IsInf(t, 0) {
		return domain.Unknown[float64]()
	}
	switch {
	case t <= lo || t >= hi:
		return domain.Known(0.0)
	case t < lo2:
		return domain.Known((t - lo) / (lo2 - lo))
	case t > hi2:
		return domain.Known((hi - t) / (hi - hi2))
	}
	return domain.Known(1.0)
}

// GrowingCells is the planted cells that grow right now: fertile and inside
// the crop's growth temperature range. Unknown while a fact is missing.
func (g CropGrowth) GrowingCells() domain.Fact[int64] {
	fertile, fk := g.FertilePlanted.Value()
	factor, tk := g.TemperatureFactor().Value()
	if !fk || !tk {
		return domain.Unknown[int64]()
	}
	if factor <= 0 {
		return domain.Known(int64(0))
	}
	return domain.Known(fertile)
}

// minFertilityFactor bounds the fertility divisor so a barren plant reads as
// a very long wait instead of an infinite one.
const minFertilityFactor = 0.01

// HarvestLeadDays is the days until the field's plants finish growing,
// counted from the mean planted growth at the current temperature and
// fertility, and extended by the calendar: crops that do not grow now wait
// GrowingDaysUntil first, and growth that outlasts the growing days left
// before the frost resumes after the non-growing stretch. It is capped at one
// year. Unknown (never zero) while a fact the walk needs is missing, no plant
// is sown, or the crop cannot grow at the current temperature with no
// growing day ahead on the calendar.
func HarvestLeadDays(g CropGrowth, growDays domain.Fact[float64], calendar domain.Fact[Calendar]) domain.Fact[float64] {
	planted, pk := g.Planted.Value()
	growth, gk := g.GrowthMean.Value()
	fertility, fk := g.FertilityMean.Value()
	days, dk := growDays.Value()
	factor, tk := g.TemperatureFactor().Value()
	c, ck := calendar.Value()
	if !pk || planted <= 0 || !gk || !fk || !dk || !tk || !ck || !c.Valid() ||
		math.IsNaN(growth) || growth < 0 || math.IsNaN(fertility) || fertility < 0 || math.IsNaN(days) || math.IsInf(days, 0) || days <= 0 {
		return domain.Unknown[float64]()
	}
	need := math.Max(0, 1-growth) * days / math.Max(minFertilityFactor, fertility)
	if factor <= 0 {
		if c.GrowingDaysUntil <= 0 {
			return domain.Unknown[float64]()
		}
		return domain.Known(math.Min(YearDays, c.GrowingDaysUntil+need))
	}
	lead := need / factor
	if lead > c.GrowingDaysRemaining {
		lead += c.NonGrowingDays
	}
	return domain.Known(math.Min(YearDays, lead))
}
