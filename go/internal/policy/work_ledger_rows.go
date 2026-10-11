package policy

import "strings"

// Ledger row sections: one per table the Grafana ledger dashboard shows.
const (
	LedgerSectionRound           = "round"
	LedgerSectionOrder           = "order"
	LedgerSectionOrphan          = "orphan"
	LedgerSectionExcluded        = "excluded"
	LedgerSectionUnmet           = "unmet"
	LedgerSectionWindow          = "window"
	LedgerSectionDeclarer        = "declarer"
	LedgerSectionExportGap       = "export_gap"
	LedgerSectionExportCandidate = "export_candidate"
	LedgerSectionExportDrop      = "export_drop"
)

// LedgerRow is one row of a ledger table: its section, its place in the
// section and its fields by name. Fields are bounded vocabulary, numbers and
// booleans; a list is one comma-joined string.
type LedgerRow struct {
	Section  string
	Position int
	Cells    map[string]any
}

// Rows flattens the view into the rows the session store keeps for Grafana,
// in the order the former Ledger tab listed them. A "round" row always leads.
func (v LedgerView) Rows() []LedgerRow {
	var rows []LedgerRow
	next := map[string]int{}
	add := func(section string, cells map[string]any) {
		rows = append(rows, LedgerRow{Section: section, Position: next[section], Cells: cells})
		next[section]++
	}
	join := func(s []string) string { return strings.Join(s, ", ") }
	add(LedgerSectionRound, map[string]any{"status": v.Status, "grace_rounds": v.GraceRounds, "abstained": v.Abstained,
		"attempts_known": v.AttemptsKnown, "orders": len(v.Orders), "orphans": len(v.Orphans), "declarers": len(v.Declarers)})
	for _, o := range v.Orders {
		c := map[string]any{"owners": join(o.Owners), "recipe": o.Recipe, "product": o.Product, "mode": o.Mode, "target": o.Target,
			"bench_kind": o.BenchKind, "worker": o.Worker, "ingredients": join(o.Ingredients), "copies": o.Copies, "state": o.State,
			"benches": join(o.Benches), "placing": join(o.Placing), "unplaced": o.Unplaced, "reason": string(o.Reason)}
		if o.Shortfall != nil {
			c["short_per_day"], c["short_reason"] = o.Shortfall.ShortPerDay, string(o.Shortfall.Reason)
		}
		if a := o.Attempt; a != nil {
			c["attempt_outcome"], c["attempt_tick"], c["attempt_code"], c["attempt_reason"] = a.Outcome, a.Tick, a.Code, a.Reason
		}
		add(LedgerSectionOrder, c)
	}
	for _, b := range v.Orphans {
		add(LedgerSectionOrphan, map[string]any{"bench": b.Bench, "bill": b.ID, "recipe": b.Recipe, "target": b.Target, "worker": b.Worker,
			"state": b.State, "rounds": b.Rounds, "grace_left": b.GraceLeft, "spent": b.Spent})
	}
	further := map[string]bool{}
	for _, k := range v.FurtherBenches {
		further[k] = true
	}
	for _, u := range v.Unmet {
		add(LedgerSectionUnmet, map[string]any{"bench_kind": u.BenchKind, "short_per_day": u.ShortPerDay, "reason": string(u.Reason), "further_bench": further[u.BenchKind]})
	}
	for _, o := range v.Orders {
		if w := o.Window; w != nil {
			c := map[string]any{"recipe": o.Recipe, "predicted_per_day": w.PredictedPerDay, "start_stock": w.StartStock, "start": w.Start,
				"ends": w.Ends, "samples": w.Samples, "carrying": w.Carrying, "met": w.Met}
			if w.ObservedGain != nil {
				c["observed_gain"] = *w.ObservedGain
			}
			add(LedgerSectionWindow, c)
		}
	}
	for _, b := range v.Excluded {
		add(LedgerSectionExcluded, map[string]any{"bench": b.Bench, "recipe": b.Recipe, "kind": b.Kind, "target": b.Target})
	}
	for _, d := range v.Declarers {
		var why []string
		for _, a := range d.Abstains {
			why = append(why, a.Concern+": "+Wording(a.Fact, ""))
		}
		add(LedgerSectionDeclarer, map[string]any{"planner": d.Name, "orders": d.Orders, "abstained": len(d.Abstains) > 0, "abstains": strings.Join(why, "; ")})
	}
	if e := v.Export; e != nil {
		add(LedgerSectionExportGap, map[string]any{"gap_known": e.GapKnown, "gap": e.Gap, "in_flight": e.InFlight, "remaining": e.Remaining})
		for _, c := range e.Candidates {
			add(LedgerSectionExportCandidate, map[string]any{"recipe": c.Recipe, "product": c.Product, "stuff": c.Stuff, "worker": c.Worker,
				"score": c.Score, "net": c.Net, "ticks": c.Ticks, "ordered": c.Ordered})
		}
		for _, d := range e.Dropped {
			add(LedgerSectionExportDrop, map[string]any{"reason": string(d.Reason), "count": d.Count})
		}
	}
	return rows
}
