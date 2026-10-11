package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Planned dashes (#2765). While extreme cold holds outdoor work, one colonist
// may make bounded trips to fetch scattered materials. PlanDash picks the trip
// set from the hypothermia estimator (OutdoorBudgetFor's formula): trips are
// ordered by value per exposed tick, each is an out-and-back leg plus a
// pickup dwell, and the set is cut where the pawn's hypothermia would reach
// DashSeverityCap by the end of the last return. Hypothermia slows the pawn
// (the Moving capacity offsets of the vanilla stages), so a leg is walked at
// the speed of the severity it starts at, not the speed the pawn left at.
//
// Every trip is counted from home with no rest between trips. The window is
// planned once, from the severity the pawn starts at, and held in
// RoundsLatches.Dash until its bound (ReviewDash); DashWindow.Until is the
// one bound every consumer reads (OpenAt). A pawn still outside at the bound
// is sheltered again, which recalls it through the Safe area.

// DashSeverityCap is the hypothermia severity a dash never reaches: the
// serious stage floor (Moving -0.3, Manipulation -0.5, pain). The
// estimator plans the whole trip set, return included, to stay below it.
const DashSeverityCap = HypothermiaSerious

// DashMarginIntervals is the headroom, in hypothermia growth intervals, kept
// under the cap for review and step granularity (the pawn is recalled at a
// review, not the instant the bound passes).
const DashMarginIntervals = 3

// DashPickupTicks is the exposed time at a pickup (stop, lift, turn).
const DashPickupTicks = 120

// DashUnboundedTicks is the window length when the cold does not grow
// hypothermia for this pawn (its clothing holds): one review horizon, renewed
// each review while the facts stand.
const DashUnboundedTicks = domain.TicksPerHour

// HypothermiaMovingFactor is the Moving capacity multiplier of the hypothermia
// stage at severity: the vanilla stages offset Moving by -0.1 from the minor
// stage (0.20), -0.3 from the serious stage (0.35) and leave it at -0.3
// through the extreme stage (0.62), where consciousness caps instead.
func HypothermiaMovingFactor(severity float64) float64 {
	switch {
	case severity >= HypothermiaSerious:
		return 0.7
	case severity >= HypothermiaMinor:
		return 0.9
	}
	return 1
}

// DashCandidate is one pickup: the one-way path in cells from home and the
// value gained by fetching it.
type DashCandidate struct {
	ID        string
	Value     float64
	PathCells float64
}

// DashTrip is one planned trip. Exposure is its planned ticks outdoors.
type DashTrip struct {
	ID       string
	Value    float64
	Exposure int
}

// DashRequest is the pure input of PlanDash: the ambient temperature, the
// pawn's comfortable minimum, current hypothermia and move speed (cells per
// second, at that severity), and the candidate pickups.
type DashRequest struct {
	Now                                      domain.Tick
	Temperature, ComfortMin, Severity, Speed domain.Fact[float64]
	Candidates                               []DashCandidate
	Tuning                                   HypothermiaTuning
}

// DashPlan is PlanDash's result. Until is the tick the last planned return
// completes; zero when no window opens.
type DashPlan struct {
	Trips  []DashTrip
	Until  domain.Tick
	Reason string
}

// Dash plan reasons.
const (
	DashPlanned    = "planned"
	DashUnknown    = "unknown_input"
	DashNoTrip     = "no_trip_fits"
	DashNoValue    = "no_candidates"
	DashUnbounded  = "cold_not_harmful"
	DashAtCapacity = "severity_at_cap"
)

// PlanDash selects the trips for one pawn. Unknown inputs open nothing.
func PlanDash(r DashRequest) DashPlan {
	temp, tk := r.Temperature.Value()
	comfort, ck := r.ComfortMin.Value()
	sev, sk := r.Severity.Value()
	speed, vk := r.Speed.Value()
	if !tk || !ck || !sk || !vk || math.IsNaN(speed) || speed <= 0 {
		return DashPlan{Reason: DashUnknown}
	}
	if len(r.Candidates) == 0 {
		return DashPlan{Reason: DashNoValue}
	}
	if sev >= DashSeverityCap {
		return DashPlan{Reason: DashAtCapacity}
	}
	growth := r.Tuning.growth(temp, comfort)
	order := append([]DashCandidate(nil), r.Candidates...)
	baseFactor := HypothermiaMovingFactor(sev)
	exposure := func(c DashCandidate) float64 {
		return 2*c.PathCells/speed*60 + DashPickupTicks
	}
	sort.SliceStable(order, func(i, j int) bool {
		a, b := order[i].Value/exposure(order[i]), order[j].Value/exposure(order[j])
		if a != b {
			return a > b
		}
		return order[i].ID < order[j].ID
	})
	var plan DashPlan
	elapsed := 0.0
	// walk advances the clock over a leg of cells and returns the severity at
	// its end; the speed is that of the severity the leg is at, step by step.
	walk := func(sev, cells float64) (float64, float64) {
		spent := 0.0
		for cells > 0 {
			v := speed * HypothermiaMovingFactor(sev) / baseFactor
			dt := 60.0
			if v*dt/60 > cells {
				dt = cells / v * 60
			}
			cells -= v * dt / 60
			spent += dt
			sev += growth * dt / hypothermiaInterval
		}
		return sev, spent
	}
	for _, c := range order {
		if c.PathCells < 0 || math.IsNaN(c.PathCells) || c.Value <= 0 {
			continue
		}
		s, out := walk(sev, c.PathCells)
		s += growth * DashPickupTicks / hypothermiaInterval
		s, back := walk(s, c.PathCells)
		if growth > 0 && s+DashMarginIntervals*growth >= DashSeverityCap {
			continue
		}
		sev = s
		spent := out + DashPickupTicks + back
		elapsed += spent
		plan.Trips = append(plan.Trips, DashTrip{ID: c.ID, Value: c.Value, Exposure: int(math.Ceil(spent))})
	}
	switch {
	case len(plan.Trips) == 0:
		plan.Reason = DashNoTrip
	case growth <= 0:
		plan.Until, plan.Reason = r.Now+DashUnboundedTicks, DashUnbounded
	default:
		plan.Until, plan.Reason = r.Now+domain.Tick(math.Ceil(elapsed)), DashPlanned
	}
	return plan
}

// DashWindow is the open window: the one pawn allowed outdoors through
// extreme cold, the trips planned, and the tick it closes.
type DashWindow struct {
	Pawn  PawnID
	Until domain.Tick
	Trips []DashTrip `json:",omitempty"`
}

// OpenAt is the single close rule: the window is open before its bound and
// closed at it. Every consumer reads this; none keeps its own timer.
func (w DashWindow) OpenAt(now domain.Tick) bool { return w.Pawn != "" && now < w.Until }

// DashOpenFor reports whether pawn may be outdoors at now.
func (f RoundsFacts) DashOpenFor(pawn PawnID) bool {
	w, known := f.Dash.Value()
	return known && w.Pawn == pawn && w.OpenAt(f.Tick)
}

// DashOpen reports whether any dash window is open at the facts' tick.
func (f RoundsFacts) DashOpen() bool {
	w, known := f.Dash.Value()
	return known && w.OpenAt(f.Tick)
}

// DashCandidates are the pickups the loot census offers: safe stacks with a
// known path and a known market value.
func DashCandidates(f RoundsFacts) []DashCandidate {
	loot, known := f.EventLoot.Value()
	if !known {
		return nil
	}
	var out []DashCandidate
	for _, row := range loot {
		if !row.SafetyKnown || !row.SafeToHaul || row.SpawnForbidden {
			continue
		}
		path, pk := row.PathLength.Value()
		unit, mk := f.Items.Market[Resource(row.Supply.Definition)]
		if !pk || !mk || row.Count <= 0 || path < 0 {
			continue
		}
		out = append(out, DashCandidate{ID: row.Supply.Thing, Value: unit * float64(row.Count), PathCells: path})
	}
	return out
}

// dashLatch is the window a review persists: none when unknown or closed.
func dashLatch(f domain.Fact[DashWindow]) DashWindow {
	w, _ := f.Value()
	return w
}

// DashWindowOf plans a window afresh: ReviewDash with none held.
func DashWindowOf(f RoundsFacts, pawns domain.Fact[[]WorkPawn], now domain.Tick, tuning HypothermiaTuning) domain.Fact[DashWindow] {
	return ReviewDash(DashWindow{}, f, pawns, now, tuning)
}

// ReviewDash is the window the review leaves. A window exists only while
// extreme cold is the sole outdoor hazard and no hostile is on the map (heat
// and fallout stay held). One planned and still before its bound is kept as
// planned: its bound was set from the severity the pawn started at, and
// re-planning from the severity it has since built up would close it under
// a pawn that is partway out. One at or past its bound closes and plans
// nothing in this review, so the pawn is recalled before another window can
// open under it. Otherwise each available colonist with a readable body is
// planned and the plan with the most value wins, ties to the lowest pawn id.
// Unknown while the hazards, hostiles or the roster are unknown.
func ReviewDash(held DashWindow, f RoundsFacts, pawns domain.Fact[[]WorkPawn], now domain.Tick, tuning HypothermiaTuning) domain.Fact[DashWindow] {
	hazards, hk := OutdoorHazardsOf(f).Value()
	hostiles, ok := f.Hostiles.Value()
	rows, pk := pawns.Value()
	if !hk || !ok || !pk {
		return domain.Unknown[DashWindow]()
	}
	if hostiles > 0 || len(hazards) != 1 || hazards[0].Kind != HazardExtremeCold {
		return domain.Known(DashWindow{})
	}
	if held.Pawn != "" {
		if held.OpenAt(now) && slices.ContainsFunc(rows, func(w WorkPawn) bool { up, known := w.Available.Value(); return w.ID == held.Pawn && known && up }) {
			return domain.Known(held)
		}
		return domain.Known(DashWindow{})
	}
	candidates := DashCandidates(f)
	var best DashWindow
	bestValue := 0.0
	for _, w := range rows {
		if up, known := w.Available.Value(); !known || !up {
			continue
		}
		body, known := w.Body.Value()
		if !known {
			continue
		}
		plan := PlanDash(DashRequest{Now: now, Temperature: f.OutdoorTemperature, ComfortMin: body.ComfortableMinC,
			Severity: body.Hypothermia, Speed: body.MoveSpeed, Candidates: candidates, Tuning: tuning})
		if len(plan.Trips) == 0 {
			continue
		}
		value := 0.0
		for _, t := range plan.Trips {
			value += t.Value
		}
		if value > bestValue || value == bestValue && w.ID < best.Pawn {
			best, bestValue = DashWindow{Pawn: w.ID, Until: plan.Until, Trips: plan.Trips}, value
		}
	}
	return domain.Known(best)
}
