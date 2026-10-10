package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The ledger view is the read-only picture of one Round's work ledger for the
// launcher: what each planner declared, where every wanted order stands against
// the benches' bills, the orphans on their way out, the dispatcher's unmet
// throughput and calibration windows, and MaintainTrade's silver gap. It is a
// projection of memory the Round already holds; nothing in it is persisted or
// fed back to a planner.

// Ledger view statuses.
const (
	// LedgerViewNone: no review has filed the ledger yet.
	LedgerViewNone = "no_review"
	// LedgerViewInactive: no planner has declared into the ledger.
	LedgerViewInactive = "inactive"
	// LedgerViewUnread: the bench readback was unread, so nothing was diffed.
	LedgerViewUnread = "readback_unknown"
	// LedgerViewReconciled: the Round declared and diffed.
	LedgerViewReconciled = "reconciled"
)

// Order states in the view.
const (
	// OrderPlaced: every wanted copy stands on a bench.
	OrderPlaced = "placed"
	// OrderPlacing: the Round's plan places at least one copy.
	OrderPlacing = "placing"
	// OrderUnplaced: at least one copy fits no bench (Reason says why).
	OrderUnplaced = "unplaced"
)

// Orphan states in the view.
const (
	// OrphanRemoving: the grace is spent; this Round's plan removes the bill.
	OrphanRemoving = "removing"
	// OrphanPending: undeclared, counting down GraceLeft Rounds.
	OrphanPending = "pending_removal"
	// OrphanHeld: undeclared, but a planner abstained so nothing is removed.
	OrphanHeld = "held_by_abstain"
)

// NamedDeclared is one planner's declaration with the planner's name.
type NamedDeclared struct {
	Name string
	Declared
}

// LedgerViewInput is what the Round holds when it builds the view.
type LedgerViewInput struct {
	Tick      int64
	Declarers []NamedDeclared
	Plan      LedgerPlan
	Placed    []LedgerPlacement
	Unplaced  []OrderSpec
	Dispatch  map[string]OrderDispatch
	Memory    DispatchMemory
	Stock     domain.Fact[map[Resource]int64]
	Unmet     []UnmetThroughput
	Further   []string
}

// LedgerView is the ledger as the launcher reads it.
type LedgerView struct {
	Status string `json:"status"`
	Tick   int64  `json:"tick"`
	// GraceRounds is OrphanGraceRounds: the Rounds an undeclared bill waits.
	GraceRounds int `json:"graceRounds"`
	// Abstained is whether any planner abstained, which stops all removal.
	Abstained      bool                 `json:"abstained"`
	Declarers      []LedgerDeclarerView `json:"declarers"`
	Orders         []LedgerOrderView    `json:"orders"`
	Orphans        []LedgerBillView     `json:"orphans"`
	Excluded       []LedgerBillView     `json:"excluded"`
	Unmet          []UnmetView          `json:"unmet"`
	FurtherBenches []string             `json:"furtherBenches"`
	Export         *ExportView          `json:"export"`
}

// LedgerDeclarerView is one planner's declaration summary.
type LedgerDeclarerView struct {
	Name    string `json:"name"`
	Orders  int    `json:"orders"`
	Abstain bool   `json:"abstain"`
}

// LedgerOrderView is one wanted order and where it stands.
type LedgerOrderView struct {
	Key         string   `json:"key"`
	Owner       string   `json:"owner"`
	Recipe      string   `json:"recipe"`
	Product     string   `json:"product"`
	Mode        string   `json:"mode"`
	Target      int32    `json:"target"`
	BenchKind   string   `json:"benchKind"`
	Worker      string   `json:"worker"`
	Ingredients []string `json:"ingredients"`
	// Copies is how many benches should carry it (the rate model).
	Copies   int      `json:"copies"`
	Eligible int      `json:"eligible"`
	State    string   `json:"state"`
	Benches  []string `json:"benches"`
	Placing  []string `json:"placing"`
	// Unplaced counts the copies no bench could take, Reason why.
	Unplaced int         `json:"unplaced"`
	Reason   UnmetReason `json:"reason"`
	Demand   *float64    `json:"demandPerDay"`
	Capacity *float64    `json:"capacityPerDay"`
	Wanted   *int        `json:"benchesWanted"`
	// Widen is the calibration's extra benches; Shortfall the closed window's
	// verdict while it holds.
	Widen     int         `json:"widen"`
	Shortfall *UnmetView  `json:"shortfall"`
	Window    *WindowView `json:"window"`
}

// WindowView is an order's calibration window: predicted against observed.
type WindowView struct {
	Open       bool  `json:"open"`
	Start      int64 `json:"start"`
	Ends       int64 `json:"ends"`
	StartStock int64 `json:"startStock"`
	Deficit    int64 `json:"deficit"`
	// PredictedPerDay is the carrying benches' units per day; ObservedGain the
	// stock gained since Start, nil while the stock is unread.
	PredictedPerDay float64 `json:"predictedPerDay"`
	ObservedGain    *int64  `json:"observedGain"`
	Carrying        int     `json:"carrying"`
	Samples         int     `json:"samples"`
	Busy            int     `json:"busy"`
	Inactive        bool    `json:"inactive"`
	Met             bool    `json:"met"`
}

// LedgerBillView is a bill standing on a bench.
type LedgerBillView struct {
	ID        string   `json:"id"`
	Bench     string   `json:"bench"`
	Kind      string   `json:"kind"`
	Recipe    string   `json:"recipe"`
	Mode      string   `json:"mode"`
	Target    int32    `json:"target"`
	Worker    string   `json:"worker"`
	Spent     bool     `json:"spent"`
	State     string   `json:"state"`
	Rounds    int      `json:"rounds"`
	GraceLeft int      `json:"graceLeft"`
	Filter    []string `json:"ingredients"`
}

// UnmetView is a throughput shortfall in product units per day.
type UnmetView struct {
	BenchKind   string      `json:"benchKind"`
	ShortPerDay float64     `json:"shortPerDay"`
	Reason      UnmetReason `json:"reason"`
}

// ExportView is MaintainTrade's latest declaration: the silver gap, what in
// flight netted off it and the best scored candidates.
type ExportView struct {
	GapKnown bool    `json:"gapKnown"`
	Gap      float64 `json:"gap"`
	InFlight float64 `json:"inFlight"`
	// Remaining is the gap left to order against once the goods in flight are
	// netted.
	Remaining  float64               `json:"remaining"`
	Candidates []ExportCandidateView `json:"candidates"`
}

// ExportCandidateView is one scored sale good; Ordered marks the ones the
// Round declared.
type ExportCandidateView struct {
	Recipe    string  `json:"recipe"`
	BenchKind string  `json:"benchKind"`
	Product   string  `json:"product"`
	Stuff     string  `json:"stuff"`
	Worker    string  `json:"worker"`
	Value     float64 `json:"value"`
	Net       float64 `json:"net"`
	Ticks     float64 `json:"ticks"`
	Score     float64 `json:"score"`
	Ordered   bool    `json:"ordered"`
}

// NewExportView is the view of one export declaration against the gap it
// read. The candidates are the best scored pairs plus every ordered one.
func NewExportView(plan ExportPlan, gap domain.Fact[float64]) *ExportView {
	v := &ExportView{InFlight: plan.InFlight, Candidates: []ExportCandidateView{}}
	g, known := gap.Value()
	v.GapKnown = known
	if known {
		v.Gap, v.Remaining = g, max(0, g-plan.InFlight)
	}
	shown := append([]ExportCandidate(nil), plan.Candidates...)
	for _, c := range plan.Ranked {
		if !sameCandidate(shown, c) {
			shown = append(shown, c)
		}
	}
	for _, c := range shown {
		v.Candidates = append(v.Candidates, ExportCandidateView{Recipe: c.Recipe, BenchKind: c.BenchKind, Product: string(c.Product), Stuff: string(c.Stuff), Worker: string(c.Worker),
			Value: c.Value, Net: c.Net, Ticks: c.Ticks, Score: c.Score, Ordered: sameCandidate(plan.Ranked, c)})
	}
	return v
}

// sameCandidate is whether list holds c, by identity: ExportCandidate carries
// slices.
func sameCandidate(list []ExportCandidate, c ExportCandidate) bool {
	for _, o := range list {
		if o.Recipe == c.Recipe && o.Stuff == c.Stuff && o.Worker == c.Worker && o.BenchKind == c.BenchKind {
			return true
		}
	}
	return false
}

// NewLedgerView is an empty view with the given status, its lists empty
// rather than null.
func NewLedgerView(status string, tick int64) LedgerView {
	return LedgerView{Status: status, Tick: tick, GraceRounds: OrphanGraceRounds,
		Declarers: []LedgerDeclarerView{}, Orders: []LedgerOrderView{}, Orphans: []LedgerBillView{}, Excluded: []LedgerBillView{},
		Unmet: []UnmetView{}, FurtherBenches: []string{}}
}

// BuildLedgerView projects one Round's ledger state. The result shares none of
// the dispatcher memory it reads.
func BuildLedgerView(in LedgerViewInput) LedgerView {
	view := NewLedgerView(LedgerViewReconciled, in.Tick)
	view.FurtherBenches = append(view.FurtherBenches, in.Further...)
	declared := make([]Declared, 0, len(in.Declarers))
	owner := map[string]string{}
	for _, d := range in.Declarers {
		declared = append(declared, d.Declared)
		view.Declarers = append(view.Declarers, LedgerDeclarerView{Name: d.Name, Orders: len(d.Orders), Abstain: d.Abstain})
		for _, o := range d.Orders {
			if _, ok := owner[o.Key()]; !ok {
				owner[o.Key()] = d.Name
			}
		}
	}
	wanted, abstain := WantedOrders(declared)
	view.Abstained = abstain
	matched := map[string][]string{}
	for _, b := range in.Plan.Matched {
		matched[b.Spec.Key()] = append(matched[b.Spec.Key()], b.Bench)
	}
	placing := map[string][]string{}
	for _, p := range in.Placed {
		placing[p.Spec.Key()] = append(placing[p.Spec.Key()], p.Bench)
	}
	unplaced := map[string]int{}
	for _, o := range in.Unplaced {
		unplaced[o.Key()]++
	}
	keys := make([]string, 0, len(wanted))
	for k := range wanted {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	stock, stockKnown := in.Stock.Value()
	for _, k := range keys {
		o, d := wanted[k], in.Dispatch[k]
		row := LedgerOrderView{Key: k, Owner: owner[k], Recipe: o.Recipe, Product: string(o.Product), Mode: string(o.Mode), Target: o.Target,
			BenchKind: o.BenchKind, Worker: o.Worker, Ingredients: append([]string{}, o.Ingredients...), Copies: max(d.Copies, 1), Eligible: d.Eligible,
			State: OrderPlaced, Benches: sortedStrings(matched[k]), Placing: sortedStrings(placing[k]), Unplaced: unplaced[k],
			Demand: factPtr(d.Demand), Capacity: factPtr(d.Capacity), Wanted: factPtr(d.Wanted), Widen: in.Memory.Widen[k]}
		switch {
		case row.Unplaced > 0:
			row.State, row.Reason = OrderUnplaced, UnmetSlotsFull
			if d.Eligible == 0 {
				row.Reason = UnmetNoBench
			}
		case len(row.Placing) > 0:
			row.State = OrderPlacing
		}
		if s, ok := in.Memory.Shortfalls[k]; ok {
			row.Shortfall = &UnmetView{BenchKind: s.BenchKind, ShortPerDay: s.ShortPerDay, Reason: s.Reason}
		}
		if w, ok := in.Memory.Windows[k]; ok {
			wv := &WindowView{Open: w.Open, Start: w.Start, Ends: w.Start + ThroughputWindowTicks, StartStock: w.StartStock, Deficit: w.Deficit, PredictedPerDay: w.PerDay,
				Carrying: w.Carrying, Samples: w.Samples, Busy: w.Busy, Inactive: w.Inactive, Met: w.Met}
			if stockKnown {
				gain := stock[o.Product] - w.StartStock
				wv.ObservedGain = &gain
			}
			row.Window = wv
		}
		view.Orders = append(view.Orders, row)
	}
	for _, b := range in.Plan.Remove {
		view.Orphans = append(view.Orphans, billView(b, OrphanRemoving, OrphanGraceRounds))
	}
	for _, b := range in.Plan.Held {
		state := OrphanPending
		if abstain {
			state = OrphanHeld
		}
		view.Orphans = append(view.Orphans, billView(b, state, in.Plan.Orphans[b.ID]))
	}
	for _, b := range in.Plan.Excluded {
		view.Excluded = append(view.Excluded, billView(b, "", 0))
	}
	for _, u := range in.Unmet {
		view.Unmet = append(view.Unmet, UnmetView(u))
	}
	return view
}

// billView is a bill row; rounds is the consecutive Rounds it has been
// undeclared.
func billView(b ActualBill, state string, rounds int) LedgerBillView {
	left := 0
	if state == OrphanPending {
		left = max(0, OrphanGraceRounds-rounds)
	}
	return LedgerBillView{ID: b.ID, Bench: b.Bench, Kind: string(b.Kind), Recipe: b.Spec.Recipe, Mode: string(b.Spec.Mode), Target: b.Spec.Target, Worker: b.Spec.Worker,
		Spent: b.Spent, State: state, Rounds: rounds, GraceLeft: left, Filter: append([]string{}, b.Spec.Ingredients...)}
}

func sortedStrings(s []string) []string {
	out := append([]string{}, s...)
	sort.Strings(out)
	return out
}

func factPtr[T any](f domain.Fact[T]) *T {
	if v, ok := f.Value(); ok {
		return &v
	}
	return nil
}
