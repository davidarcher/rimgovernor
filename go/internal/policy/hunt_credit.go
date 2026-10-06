package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HuntSource is the one counter group of hunting: every hunt channel shares
// it, and the ledger's kills and butchers are its deliveries.
const HuntSource = "hunt"

var ticksPerDay = float64(domain.TicksPerDay)

const (
	// HuntCreditWindowDays is the trailing window hunting is judged over, long
	// enough that a kill every few days averages out.
	HuntCreditWindowDays = 6.0
	// HuntKillsPerHunterDay is the planning kill rate of one hunter until the
	// ledger shows a cadence: a hunt's walk, kill and haul take most of a day.
	HuntKillsPerHunterDay = 1.0
	// HuntCadenceDays bounds the trailing kills the cadence reads, and
	// HuntCadenceMinKills is the history it needs before it replaces the default.
	HuntCadenceDays     = 14.0
	HuntCadenceMinKills = 3
)

// HuntKill is a ledger kill: the corpse's thing id (the food census's stock
// id), the meat the corpse would yield and the tick of the kill.
type HuntKill struct {
	CorpseID  string
	Potential float64
	Tick      int64
}

// HuntButcher is a ledger butcher record: the meat a corpse's butchering made.
type HuntButcher struct {
	CorpseID      string
	MeatNutrition float64
}

// HuntDelivered is the hunt group's credited nutrition, in the ledger's
// cumulative form, from the kills and butchers of one load token and the
// corpse census. A corpse is credited once, by its id, in two stages:
//
//   - Kill: while it lies unbutchered in the census (field or larder; hauling
//     never counts) it is worth its potential yield times its rot clock, the
//     rot ticks left over the most it was seen with. A corpse with no rot
//     deadline (frozen, or a non-perishable) does not decay. A corpse the
//     census no longer lists is worth nothing; with an incomplete census it is
//     held at its yield rather than read as gone.
//   - Butcher: the meat its butchering made replaces the corpse's credit.
//
// Humanlike corpses are the human-butchery channel's, never a kill's. The
// ledger holds the newest records only, so a kill that fell off it stops
// counting. A nil credit credits nothing.
func (d *DeliveryCredit) HuntDelivered(kills []HuntKill, butchers []HuntButcher, stocks []FoodStock, complete bool) float64 {
	if d == nil {
		return 0
	}
	made := map[string]float64{}
	for _, b := range butchers {
		made[b.CorpseID] += b.MeatNutrition
	}
	corpses := map[string]FoodStock{}
	for _, s := range stocks {
		if s.Corpse {
			corpses[s.ID] = s
		}
	}
	peak := map[string]int64{}
	counted := map[string]bool{}
	total := 0.0
	for _, k := range kills {
		if counted[k.CorpseID] || !foodNumber(k.Potential) {
			continue
		}
		counted[k.CorpseID] = true
		corpse, listed := corpses[k.CorpseID]
		if listed && corpse.IsHumanlike {
			continue
		}
		credit := k.Potential
		switch meat, butchered := made[k.CorpseID]; {
		case butchered:
			credit = meat
		case listed:
			credit *= d.rotFraction(corpse, peak)
		case complete:
			credit = 0
		}
		total += math.Max(0, credit)
	}
	d.rotPeak = peak
	return total
}

// rotFraction is the share of a corpse's rot clock left: its rot ticks over
// the most seen for it, carried in peak into the next call.
func (d *DeliveryCredit) rotFraction(s FoodStock, peak map[string]int64) float64 {
	perishable, pk := s.Perishable.Value()
	ticks, tk := s.RotTicks.Value()
	if !pk || !perishable || !tk {
		return 1
	}
	most := max(ticks, d.rotPeak[s.ID])
	peak[s.ID] = most
	if ticks <= 0 || most <= 0 {
		return 0
	}
	return float64(ticks) / float64(most)
}

// HuntCadence is the kills per hunter-day the ledger shows: the kills of the
// trailing HuntCadenceDays over the hunters and the days that history spans.
// Without HuntCadenceMinKills of them it is HuntKillsPerHunterDay.
func HuntCadence(kills []HuntKill, hunters int, now int64) float64 {
	if hunters <= 0 {
		return HuntKillsPerHunterDay
	}
	cutoff := now - int64(HuntCadenceDays*ticksPerDay)
	n, oldest := 0, now
	for _, k := range kills {
		if k.Tick >= cutoff {
			n++
			oldest = min(oldest, k.Tick)
		}
	}
	if n < HuntCadenceMinKills {
		return HuntKillsPerHunterDay
	}
	span := math.Min(HuntCadenceDays, math.Max(1, float64(now-oldest)/ticksPerDay))
	return float64(n) / (float64(hunters) * span)
}

// HuntThroughput caps the rate of the hunt channels at what the hunters kill:
// hunters x kills per hunter-day x the meat of an average prey. Each channel's
// nutrition, work and products per day scale together by the share the cap
// leaves, so a channel the plan opens keeps its labor per nutrition. The
// channels' StockCap (the animals in reach, huntChannel) bounds them again in
// the ranker as a finite source. Without hunters, or for a channel that waits
// on a hunter's weapon, nothing scales.
func HuntThroughput(channels []SupplyCandidate, hunters int, killsPerHunterDay float64) []SupplyCandidate {
	if hunters <= 0 || killsPerHunterDay <= 0 {
		return channels
	}
	rate, prey := 0.0, 0.0
	for _, c := range channels {
		if huntScales(c) {
			n, _ := c.Nutrition().PerDay.Value()
			rate += n
			prey += float64(huntPrey(c))
		}
	}
	if rate <= 0 || prey <= 0 {
		return channels
	}
	limit := float64(hunters) * killsPerHunterDay * rate * FoodHuntCycleDays / prey
	if limit >= rate {
		return channels
	}
	share := limit / rate
	out := make([]SupplyCandidate, len(channels))
	copy(out, channels)
	for i, c := range out {
		if !huntScales(c) {
			continue
		}
		w, _ := c.LaborPerDay.Value()
		c.Yields = append([]CandidateYield(nil), c.Yields...)
		for j, y := range c.Yields {
			if pd, known := y.PerDay.Value(); known {
				c.Yields[j].PerDay = domain.Known(pd * share)
			}
		}
		c.LaborPerDay = domain.Known(w * share)
		c.Terms = append(append([]CandidateTerm(nil), c.Terms...), CandidateTerm{"hunt_throughput_share", share})
		out[i] = c
	}
	return out
}

// huntScales is whether a hunt channel counts toward the hunters' throughput.
func huntScales(c SupplyCandidate) bool {
	if c.Kind != CandidateHunt {
		return false
	}
	n, known := c.Nutrition().PerDay.Value()
	if !known || n <= 0 {
		return false
	}
	for _, t := range c.Terms {
		if t.Name == termNeedsWeapon {
			return false
		}
	}
	return true
}

func huntPrey(c SupplyCandidate) int {
	for _, t := range c.Terms {
		if t.Name == "prey" {
			return int(t.Value)
		}
	}
	return 1
}
