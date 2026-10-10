package main

import (
	"fmt"
	"math"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The Ledger tab's view-model: the work ledger serve reports at /api/ledger
// as tables of strings, so the page holds no labels or ordering. Read-only:
// the ledger is the controller's memory and the bot stays authoritative; the
// tab has no control that changes it.

// LedgerRow is one table row; Flag is "warn" for a row that needs a look.
type LedgerRow struct {
	Cells []string
	Flag  string
}

// LedgerSection is a titled table. Note says what an empty table means.
type LedgerSection struct {
	Title   string
	Note    string
	Columns []string
	Rows    []LedgerRow
}

// LedgerPage is the Ledger tab. Feed is the ledger feed's: with no value the
// page shows its Notice alone.
type LedgerPage struct {
	Feed
	Headline string
	Sections []LedgerSection
}

var modeLabels = map[string]string{
	"stock_target": "stock target", "food_target": "stock target", "beer_reserve": "stock target",
	"gear_batch": "finite batch", "butcher_forever": "forever", "human_butcher_forever": "forever",
}

var orderStateLabels = map[string]string{
	policy.OrderPlaced: "placed", policy.OrderPlacing: "placing this Round", policy.OrderUnplaced: "unplaced",
}

var unmetLabels = map[policy.UnmetReason]string{
	policy.UnmetNoBench:          "no bench for it (no_bench)",
	policy.UnmetSlotsFull:        "every bench is full (bench_slots_full)",
	policy.UnmetIngredients:      "ingredients are out (ingredient_bound)",
	policy.UnmetHaul:             "hauling holds it up (haul_bound)",
	policy.UnmetBenchesExhausted: "a bill on every bench, still short (benches_exhausted)",
}

func unmetLabel(r policy.UnmetReason) string {
	if l, ok := unmetLabels[r]; ok {
		return l
	}
	return string(r)
}

func perDay(v float64) string {
	return strconv.FormatFloat(math.Round(v*10)/10, 'f', -1, 64) + "/day"
}

func orDash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

func list(s []string) string { return orDash(strings.Join(s, ", ")) }

func ledgerPage(r Reading[policy.LedgerView]) LedgerPage {
	p := LedgerPage{Feed: feedOf(r, "The work ledger"), Sections: []LedgerSection{}}
	v := r.Value
	if v == nil {
		return p
	}
	switch v.Status {
	case policy.LedgerViewNone:
		p.Headline = "No review has filed the ledger yet."
	case policy.LedgerViewInactive:
		p.Headline = "No planner declares into the ledger: it reads nothing and removes nothing."
	case policy.LedgerViewUnread:
		p.Headline = fmt.Sprintf("Bench bills were unread at tick %s: nothing was diffed this Round.", group(v.Tick))
	default:
		p.Headline = fmt.Sprintf("Round at tick %s: %d wanted order(s), %d orphan bill(s), %d planner(s) declaring.", group(v.Tick), len(v.Orders), len(v.Orphans), len(v.Declarers))
		if v.Abstained {
			p.Headline += " A planner abstained, so no orphan is removed this Round."
		}
	}

	declarers := LedgerSection{Title: "Planners declaring", Columns: []string{"Planner", "Orders", "State"}}
	for _, d := range v.Declarers {
		row := LedgerRow{Cells: []string{d.Name, strconv.Itoa(d.Orders), "declared"}}
		if d.Abstain {
			row.Cells[2], row.Flag = "abstained: a fact it needs was unread", "warn"
		}
		declarers.Rows = append(declarers.Rows, row)
	}
	declarers.Note = "Orphan bills are removed only after " + strconv.Itoa(v.GraceRounds) + " undeclared Rounds, and never in a Round where a planner abstained."

	orders := LedgerSection{Title: "Declared orders", Columns: []string{"Owner", "Order", "Target", "Mode", "Bench kind", "Worker", "Ingredients", "State", "Benches"}}
	for _, o := range v.Orders {
		name := o.Recipe
		if o.Product != "" {
			name += " (" + o.Product + ")"
		}
		state := orderStateLabels[o.State]
		benches := list(o.Benches)
		if len(o.Placing) > 0 {
			benches += " -> placing on " + strings.Join(o.Placing, ", ")
		}
		row := LedgerRow{Cells: []string{o.Owner, name, strconv.Itoa(int(o.Target)), modeLabels[o.Mode], o.BenchKind, orDash(o.Worker), list(o.Ingredients), state, benches}}
		if o.Copies > 1 {
			row.Cells[1] += fmt.Sprintf(" x%d benches", o.Copies)
		}
		if o.State == policy.OrderUnplaced {
			row.Cells[7] = fmt.Sprintf("unplaced x%d: %s", o.Unplaced, unmetLabel(o.Reason))
			row.Flag = "warn"
		}
		if o.Shortfall != nil {
			row.Cells[7] += "; short " + perDay(o.Shortfall.ShortPerDay) + ": " + unmetLabel(o.Shortfall.Reason)
			row.Flag = "warn"
		}
		orders.Rows = append(orders.Rows, row)
	}
	orders.Note = emptyNote(len(orders.Rows), "No planner has declared an order this Round.")

	orphans := LedgerSection{Title: "Orphan bills", Columns: []string{"Bench", "Bill", "Target", "Worker", "State"}}
	for _, b := range v.Orphans {
		state := ""
		switch b.State {
		case policy.OrphanRemoving:
			state = "removed by this Round's plan"
		case policy.OrphanHeld:
			state = "undeclared, held: a planner abstained"
		default:
			state = fmt.Sprintf("undeclared %d of %d Rounds: %d left before removal", b.Rounds, v.GraceRounds, b.GraceLeft)
		}
		if b.Spent {
			state += " (finished)"
		}
		orphans.Rows = append(orphans.Rows, LedgerRow{Cells: []string{b.Bench, b.Recipe, strconv.Itoa(int(b.Target)), orDash(b.Worker), state}, Flag: "warn"})
	}
	orphans.Note = emptyNote(len(orphans.Rows), "No bill on a bench is undeclared.")

	excluded := LedgerSection{Title: "Never removed", Columns: []string{"Bench", "Bill", "Kind", "Target"}}
	for _, b := range v.Excluded {
		excluded.Rows = append(excluded.Rows, LedgerRow{Cells: []string{b.Bench, b.Recipe, b.Kind, strconv.Itoa(int(b.Target))}})
	}
	excluded.Note = emptyNote(len(excluded.Rows), "No surgery, medicine, baby food or mech gestation bill stands. The ledger never removes those kinds.")

	unmet := LedgerSection{Title: "Unmet throughput", Columns: []string{"Bench kind", "Short", "Why", "Further bench"}}
	further := map[string]bool{}
	for _, k := range v.FurtherBenches {
		further[k] = true
	}
	for _, u := range v.Unmet {
		ask := "no"
		if further[u.BenchKind] {
			ask = "requested"
		}
		unmet.Rows = append(unmet.Rows, LedgerRow{Cells: []string{u.BenchKind, perDay(u.ShortPerDay), unmetLabel(u.Reason), ask}, Flag: "warn"})
	}
	unmet.Note = emptyNote(len(unmet.Rows), "Every bench kind keeps up with its orders.")

	windows := LedgerSection{Title: "Calibration windows", Columns: []string{"Order", "Predicted", "Observed gain", "Window", "Benches"}}
	for _, o := range v.Orders {
		w := o.Window
		if w == nil {
			continue
		}
		observed := "unread"
		if w.ObservedGain != nil {
			observed = fmt.Sprintf("%+d since stock %d", *w.ObservedGain, w.StartStock)
		}
		state := fmt.Sprintf("tick %s to %s, %d sample(s)", group(w.Start), group(w.Ends), w.Samples)
		if w.Met {
			state += ", target met"
		}
		windows.Rows = append(windows.Rows, LedgerRow{Cells: []string{o.Recipe, perDay(w.PredictedPerDay), observed, state, strconv.Itoa(w.Carrying)}})
	}
	windows.Note = emptyNote(len(windows.Rows), "No stock-target order is being measured.")

	p.Sections = []LedgerSection{orders, orphans, unmet, windows, excluded, exportSection(v.Export), declarers}
	return p
}

func emptyNote(rows int, text string) string {
	if rows == 0 {
		return text
	}
	return ""
}

func exportSection(e *policy.ExportView) LedgerSection {
	s := LedgerSection{Title: "Silver gap and export candidates", Columns: []string{"Recipe", "Product", "Stuff", "Worker", "Score", "Net per piece", "Ticks", "Ordered"}}
	switch {
	case e == nil:
		s.Note = "MaintainTrade has not declared in this world."
		return s
	case !e.GapKnown:
		s.Note = "The silver gap is unknown, so no export is ordered."
		return s
	case e.Gap <= 0:
		s.Note = "No silver gap: nothing is made for sale."
	default:
		s.Note = fmt.Sprintf("Silver gap %s; %s already in flight (held, packed or on standing bills); %s left to order against.", silver(e.Gap), silver(e.InFlight), silver(e.Remaining))
	}
	for _, c := range e.Candidates {
		ordered := "no"
		if c.Ordered {
			ordered = "yes"
		}
		s.Rows = append(s.Rows, LedgerRow{Cells: []string{c.Recipe, c.Product, orDash(c.Stuff), orDash(c.Worker), strconv.FormatFloat(math.Round(c.Score*1000)/1000, 'f', -1, 64), silver(c.Net), group(int64(math.Round(c.Ticks))), ordered}})
	}
	return s
}

func silver(v float64) string { return group(int64(math.Round(v))) + " silver" }
