package policy

import (
	"errors"
	"math"
)

const gearTwelfthTicks = 300000

// GearClimate is optional. A nil value preserves ambient-only scoring.
type GearClimate struct {
	Temperatures       []float64
	CurrentTwelfth     int
	TicksToNextTwelfth int64
	Weather            *GearWeather
}

type GearWeather struct {
	Definition        string
	RemainingTicks    int64 // -1 means permanent
	TemperatureOffset float64
}

func (c *GearClimate) Validate() error {
	if c == nil {
		return nil
	}
	if len(c.Temperatures) != 12 || c.CurrentTwelfth < 0 || c.CurrentTwelfth >= 12 || c.TicksToNextTwelfth <= 0 || c.TicksToNextTwelfth > gearTwelfthTicks {
		return errors.New("invalid gear seasonal curve")
	}
	for _, n := range c.Temperatures {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("invalid gear seasonal temperature")
		}
	}
	if w := c.Weather; w != nil {
		if (w.Definition != "ColdSnap" && w.Definition != "HeatWave") || w.RemainingTicks < -1 || math.IsNaN(w.TemperatureOffset) || math.IsInf(w.TemperatureOffset, 0) || w.Definition == "ColdSnap" && w.TemperatureOffset > 0 || w.Definition == "HeatWave" && w.TemperatureOffset < 0 {
			return errors.New("invalid gear weather condition")
		}
	}
	return nil
}

// TemperatureRange includes now and the next two twelfths (ten days of
// tailoring lead time). Weather affects only twelfths it still overlaps;
// longer conditions extend the target through their remaining duration.
func (c *GearClimate) TemperatureRange(ambient float64) (float64, float64) {
	low, high := ambient, ambient
	if c == nil {
		return low, high
	}
	for ahead := 0; ahead < 12; ahead++ {
		start := int64(0)
		if ahead > 0 {
			start = c.TicksToNextTwelfth + int64(ahead-1)*gearTwelfthTicks
		}
		w := c.Weather
		active := w != nil && (w.RemainingTicks == -1 || w.RemainingTicks > start)
		if ahead > 2 && !active {
			break
		}
		temperature := c.Temperatures[(c.CurrentTwelfth+ahead)%12]
		if ahead <= 2 {
			low, high = math.Min(low, temperature), math.Max(high, temperature)
		}
		if active {
			temperature += w.TemperatureOffset
			low, high = math.Min(low, temperature), math.Max(high, temperature)
		}
	}
	return low, high
}
