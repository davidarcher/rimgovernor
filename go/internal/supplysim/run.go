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
}

// Starved reports whether any of g went unmet.
func (r Report) Starved(g Good) bool { _, ok := r.FirstStarved[g]; return ok }

type runState struct {
	stock   map[Good]float64
	sources []Source
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
	r := &runState{stock: map[Good]float64{}}
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
		if p != nil {
			r.command(p.Plan(r.view(w, day)), day)
		}
		for i := range r.sources {
			r.sources[i].arrive(day)
		}
		dr := DayReport{Day: day, Delivered: map[string]float64{}, Unmet: map[Good]float64{}}
		r.deliver(w, day, dr.Delivered)
		r.consume(w, day, dr.Unmet)
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
		for g, d := range demand(w, day+1) {
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
		Demand: demand(w, day), Runway: map[Good]float64{}, LaborBudget: w.LaborBudget()}
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
	return v
}

// deliver pays out every source that can work today; when their labor exceeds
// the budget every delivery is scaled by the same fraction.
func (r *runState) deliver(w World, day int, out map[string]float64) {
	avail := make([]float64, len(r.sources))
	var labor float64
	for i := range r.sources {
		s := &r.sources[i]
		a := s.available(day)
		for _, c := range s.Costs {
			if c.PerUnit > 0 {
				a = min(a, r.stock[c.Good]/c.PerUnit)
			}
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

func (r *runState) consume(w World, day int, unmet map[Good]float64) {
	for g, d := range demand(w, day) {
		if r.stock[g] >= d {
			r.stock[g] -= d
			continue
		}
		if short := d - r.stock[g]; short > 1e-9 {
			unmet[g] = short
		}
		r.stock[g] = 0
	}
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
