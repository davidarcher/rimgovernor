package supplysim

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DayReport is the end-of-day state.
type DayReport struct {
	Day       int
	Stock     map[Good]float64
	Runway    map[Good]float64 // days of stock at the next day's demand
	Delivered map[string]float64
	Unmet     map[Good]float64
}

// Report is the result of a run.
type Report struct {
	Days []DayReport
	// FirstStarved is the first day each good went unmet; goods never short
	// are absent.
	FirstStarved map[Good]int
	StarvedDays  map[Good]int
	Delivered    map[string]float64 // total per source
	MinRunway    map[Good]float64
	// BuildDone is the day each build was paid in full.
	BuildDone map[string]int `json:",omitempty"`
}

// Starved reports whether any of g went unmet.
func (r Report) Starved(g Good) bool { _, ok := r.FirstStarved[g]; return ok }

type runState struct {
	stock   map[Good]float64
	sources []Source

	threatUntil int
	powerFactor float64
	powerUntil  int
	capLimit    map[Good]float64
	capUntil    map[Good]int
	surge       map[Good]surge
	builds      []*buildState
	latched     map[Good]bool
	used        []map[Good]float64 // consumption per finished day
}

func (r *runState) threat(day int) bool { return day < r.threatUntil }

func (r *runState) power(w World, day int) float64 {
	if day < r.powerUntil {
		return w.Power * r.powerFactor
	}
	return w.Power
}

// room is the headroom for g under the storage ceiling, +Inf when unbounded.
func (r *runState) room(w World, g Good, day int) float64 {
	limit, ok := w.Capacity[g]
	if l, hit := r.capLimit[g]; hit && day < r.capUntil[g] {
		limit, ok = l, true
	}
	if !ok {
		return math.Inf(1)
	}
	return max(0, limit-r.stock[g])
}

// demand is the day's draw per good including active surges.
func (r *runState) demand(w World, day int) map[Good]float64 {
	d := demand(w, day)
	for g, s := range r.surge {
		if day < s.until {
			d[g] *= s.factor
		}
	}
	return d
}

func (r *runState) source(id string) *Source {
	for i := range r.sources {
		if r.sources[i].ID == id {
			return &r.sources[i]
		}
	}
	return nil
}

// Run simulates days days of w under p (nil opens nothing beyond each source's
// initial state). It is deterministic in w.Seed.
func Run(w World, p Planner, days int) Report {
	r := &runState{stock: map[Good]float64{}, capLimit: map[Good]float64{}, capUntil: map[Good]int{},
		surge: map[Good]surge{}, latched: map[Good]bool{}}
	for _, b := range w.Builds {
		left := map[Good]float64{}
		for _, c := range b.Costs {
			left[c.Good] += c.PerUnit
		}
		r.builds = append(r.builds, &buildState{Build: b, left: left})
	}
	for g, v := range w.Stock {
		r.stock[g] = v
	}
	for i, s := range w.Sources {
		s = s.clone()
		s.capScale, s.rng = 1, newRNG(w.Seed, i)
		r.sources = append(r.sources, s)
	}
	rep := Report{FirstStarved: map[Good]int{}, StarvedDays: map[Good]int{}, Delivered: map[string]float64{}, MinRunway: map[Good]float64{}}
	for day := 0; day < days; day++ {
		for _, sh := range w.Shocks {
			if sh.Day == day {
				sh.apply(r)
			}
		}
		r.latch(w)
		if p != nil {
			r.command(p.Plan(r.view(w, day)), day)
		}
		for i := range r.sources {
			r.sources[i].arrive(day)
		}
		dr := DayReport{Day: day, Delivered: map[string]float64{}, Unmet: map[Good]float64{}}
		r.deliver(w, day, dr.Delivered)
		r.consume(w, day, dr.Unmet, &rep)
		for g, f := range w.Spoilage {
			r.stock[g] *= 1 - clamp01(f)
		}
		for i := range r.sources {
			r.sources[i].grow(day)
		}
		dr.Stock, dr.Runway = map[Good]float64{}, map[Good]float64{}
		for g, v := range r.stock {
			dr.Stock[g] = round(v)
		}
		for g, d := range r.demand(w, day+1) {
			if d > 0 {
				rw := round(r.stock[g] / d)
				dr.Runway[g] = rw
				if m, ok := rep.MinRunway[g]; !ok || rw < m {
					rep.MinRunway[g] = rw
				}
			}
		}
		for g, u := range dr.Unmet {
			dr.Unmet[g] = round(u)
			if _, ok := rep.FirstStarved[g]; !ok {
				rep.FirstStarved[g] = day
			}
			rep.StarvedDays[g]++
		}
		rep.Days = append(rep.Days, dr)
	}
	for _, s := range r.sources {
		rep.Delivered[s.ID] = round(s.delivered)
	}
	return rep
}

func (r *runState) command(cmds []Command, day int) {
	for _, c := range cmds {
		s := r.source(c.Source)
		if s == nil || s.removed {
			continue
		}
		switch {
		case c.Kind == Open && !s.Open:
			s.Open, s.openedDay = true, day
		case c.Kind == Close:
			s.Open = false
		}
	}
}

func (r *runState) view(w World, day int) WorldView {
	v := WorldView{Day: day, Tick: domain.Tick(day) * domain.TicksPerDay, Stock: map[Good]float64{},
		Demand: r.demand(w, day), Runway: map[Good]float64{}, LaborBudget: w.LaborBudget(), Threat: r.threat(day)}
	for g, s := range r.stock {
		v.Stock[g] = s
	}
	for g, d := range v.Demand {
		if d > 0 {
			v.Runway[g] = r.stock[g] / d
		}
	}
	for i := range r.sources {
		v.Sources = append(v.Sources, r.sources[i].view(day))
	}
	for _, f := range w.Floors {
		fv := FloorView{Good: f.Good, Latched: r.latched[f.Good], Over: r.stock[f.Good] > f.Max, Min: f.Min, Target: f.Target, Max: f.Max}
		if fv.Latched {
			fv.Ask = max(0, f.Target-r.stock[f.Good])
		}
		v.Floors = append(v.Floors, fv)
		if fc, ok := r.forecast(f, day); ok {
			if v.Forecast == nil {
				v.Forecast = map[Good]Forecast{}
			}
			v.Forecast[f.Good] = fc
		}
	}
	for _, b := range r.builds {
		if !b.done && day >= b.Day {
			left := map[Good]float64{}
			for g, n := range b.left {
				left[g] = n
			}
			v.Builds = append(v.Builds, BuildView{Name: b.Name, Left: left})
		}
	}
	return v
}

// latch updates each floor's latch from the stock: on below Min, off above
// Target (the policy wood latch, rounds.go latchValue).
func (r *runState) latch(w World) {
	for _, f := range w.Floors {
		v := r.stock[f.Good]
		if r.latched[f.Good] {
			r.latched[f.Good] = v <= f.Target
		} else {
			r.latched[f.Good] = v < f.Min
		}
	}
}

// forecast mirrors policy.ForecastResourceRunway: the consumption rate over up
// to ForecastHistoryDays finished days, and stock above the floor's Min
// reserve plus unopened finite ore over that rate against the horizon. Under
// one day of history there is no forecast.
func (r *runState) forecast(f Floor, day int) (Forecast, bool) {
	n := min(day, ForecastHistoryDays)
	if n < 1 {
		return Forecast{}, false
	}
	var used float64
	for _, u := range r.used[len(r.used)-n:] {
		used += u[f.Good]
	}
	rate := used / float64(n)
	fc := Forecast{ConsumptionPerDay: rate, DaysLeft: -1}
	if rate == 0 {
		fc.Deficit = r.stock[f.Good] < f.Min
		return fc, true
	}
	usable := max(0, r.stock[f.Good]-f.Min)
	for i := range r.sources {
		s := &r.sources[i]
		if s.Finite && !s.Open && !s.removed {
			for _, y := range s.Yields {
				if y.Good == f.Good {
					usable += s.Stock * y.PerUnit
				}
			}
		}
	}
	fc.DaysLeft = usable / rate
	fc.Deficit = fc.DaysLeft < ForecastHorizonDays
	return fc, true
}

// deliver pays out every source that can work today; when their labor exceeds
// the budget every delivery is scaled by the same fraction.
func (r *runState) deliver(w World, day int, out map[string]float64) {
	avail := make([]float64, len(r.sources))
	var labor float64
	power := r.power(w, day)
	room := map[Good]float64{}
	for i := range r.sources {
		s := &r.sources[i]
		a := s.available(day)
		if s.Safe && r.threat(day) {
			a = 0
		}
		if a > 0 && s.PowerDraw > 0 {
			if power < s.PowerDraw {
				a = 0
			} else {
				power -= s.PowerDraw
			}
		}
		for _, c := range s.Costs {
			if c.PerUnit > 0 {
				a = min(a, r.stock[c.Good]/c.PerUnit)
			}
		}
		for _, y := range s.Yields {
			if _, ok := room[y.Good]; !ok {
				room[y.Good] = r.room(w, y.Good, day)
			}
			if y.PerUnit > 0 {
				a = min(a, room[y.Good]/y.PerUnit)
			}
		}
		for _, y := range s.Yields {
			room[y.Good] -= a * y.PerUnit
		}
		avail[i] = a
		if a > 0 {
			labor += s.Labor
		}
	}
	frac := 1.0
	if labor > 0 {
		frac = min(1, w.LaborBudget()/labor)
	}
	for i := range r.sources {
		s := &r.sources[i]
		taken := avail[i] * frac
		if taken <= 0 {
			s.last = 0
			continue
		}
		for _, y := range s.Yields {
			r.stock[y.Good] += taken * y.PerUnit
		}
		for _, c := range s.Costs {
			r.stock[c.Good] -= taken * c.PerUnit
		}
		s.take(taken)
		out[s.ID] = round(taken)
	}
}

func (r *runState) consume(w World, day int, unmet map[Good]float64, rep *Report) {
	used := map[Good]float64{}
	for g, d := range r.demand(w, day) {
		if r.stock[g] >= d {
			r.stock[g] -= d
			used[g] += d
			continue
		}
		if short := d - r.stock[g]; short > 1e-9 {
			unmet[g] = short
		}
		used[g] += r.stock[g]
		r.stock[g] = 0
	}
	// Builds draw what stock allows; a shortfall stays open, not unmet.
	for _, b := range r.builds {
		if b.done || day < b.Day {
			continue
		}
		b.done = true
		for g, n := range b.left {
			pay := min(n, r.stock[g])
			r.stock[g] -= pay
			used[g] += pay
			b.left[g] = n - pay
			if b.left[g] > 1e-9 {
				b.done = false
			}
		}
		if b.done {
			if rep.BuildDone == nil {
				rep.BuildDone = map[string]int{}
			}
			rep.BuildDone[b.Name] = day
		}
	}
	r.used = append(r.used, used)
}

func demand(w World, day int) map[Good]float64 {
	d := map[Good]float64{}
	for _, c := range w.Consumers {
		d[c.Good] += max(0, c.Demand(day))
	}
	return d
}

// round trims float noise so reports compare across platforms.
func round(v float64) float64 { return math.Round(v*1e6) / 1e6 }
