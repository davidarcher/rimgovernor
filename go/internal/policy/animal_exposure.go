package policy

import (
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ErrAnimalExposure marks an exposure view that could not be answered: a
// race with no comfort range, an unread condition census or an unread outdoor
// temperature. The wrapped text names what is missing (#1868, epic #1646).
var ErrAnimalExposure = errors.New("animal exposure")

// ExposureCause is why a race is in danger now.
type ExposureCause string

const (
	// ExposureCold is an active ColdSnap or an outdoor temperature below the
	// race's comfortable minimum.
	ExposureCold ExposureCause = "cold"
	// ExposureHeat is an active HeatWave or an outdoor temperature above the
	// race's comfortable maximum.
	ExposureHeat ExposureCause = "heat"
	// ExposureFallout is an active ToxicFallout.
	ExposureFallout ExposureCause = "fallout"
)

// AnimalComfort is a race's comfortable outdoor temperature range in degrees
// Celsius (ComfyTemperatureMin/Max stat bases).
type AnimalComfort struct{ Min, Max float64 }

// AnimalExposure is whether one race is in danger outdoors now; Causes is
// empty when it is not.
type AnimalExposure struct {
	Race   Resource
	Causes []ExposureCause
}

// Danger reports whether any cause applies.
func (e AnimalExposure) Danger() bool { return len(e.Causes) > 0 }

// AnimalExposures answers, per race (sorted, duplicates folded), whether it is
// in danger now: an active ColdSnap, HeatWave or ToxicFallout condition, or an
// outdoor temperature outside the race's comfort range (the range itself is
// comfortable). comfort reads a race's range from the def mirror. An unread
// census, temperature or range is an error wrapping ErrAnimalExposure; the
// temperature is only required when there is a race to judge.
func AnimalExposures(races []Resource, conditions domain.Fact[[]DisasterCondition], outdoorC domain.Fact[float64], comfort func(Resource) (AnimalComfort, error)) ([]AnimalExposure, error) {
	rows, known := conditions.Value()
	if !known {
		return nil, fmt.Errorf("%w: the disaster condition census is unread", ErrAnimalExposure)
	}
	var active []ExposureCause
	for _, c := range rows {
		cause := ExposureCause("")
		switch c.Definition {
		case ConditionColdSnap:
			cause = ExposureCold
		case ConditionHeatWave:
			cause = ExposureHeat
		case ConditionToxicFallout:
			cause = ExposureFallout
		}
		if cause != "" && !slices.Contains(active, cause) {
			active = append(active, cause)
		}
	}
	sorted := slices.Clone(races)
	slices.Sort(sorted)
	sorted = slices.Compact(sorted)
	if len(sorted) == 0 {
		return nil, nil
	}
	temperature, known := outdoorC.Value()
	if !known {
		return nil, fmt.Errorf("%w: the outdoor temperature is unread", ErrAnimalExposure)
	}
	out := make([]AnimalExposure, 0, len(sorted))
	for _, race := range sorted {
		r, err := comfort(race)
		if err != nil {
			return nil, fmt.Errorf("%w: race %s: %w", ErrAnimalExposure, race, err)
		}
		causes := slices.Clone(active)
		if temperature < r.Min && !slices.Contains(causes, ExposureCold) {
			causes = append(causes, ExposureCold)
		}
		if temperature > r.Max && !slices.Contains(causes, ExposureHeat) {
			causes = append(causes, ExposureHeat)
		}
		slices.Sort(causes)
		out = append(out, AnimalExposure{Race: race, Causes: causes})
	}
	return out, nil
}
