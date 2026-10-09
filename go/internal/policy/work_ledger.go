package policy

import (
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OrphanGraceRounds is how many consecutive Rounds a bill that no planner
// declared must stay undeclared before Reconcile removes it. The counters are
// memory only: a restart empties them and only delays removal.
const OrphanGraceRounds = 3

// OrderSpec is a production order's identity. It carries no native tag: a
// bill on a bench is the same order as a declared one when their specs have
// the same Key, so a restart rebuilds the ledger from native readback.
type OrderSpec struct {
	Recipe string
	// Ingredients is the ingredient filter; order is not identity.
	Ingredients []string
	// Worker pins one pawn, empty for any.
	Worker string
	Mode   domain.BillMode
	Target int32
	// BenchKind is the workbench definition, not a particular bench.
	BenchKind string
	// Product and Class are the declarer's stock-target context for the
	// dispatcher: the resource the order stocks and how fast a shortfall must
	// refill. They are not identity (not in Key) and a bill read back from a
	// bench has neither.
	Product Resource
	Class   ResourceClass
}

// Key is the canonical spec identity. The mode is its wire class: native tells
// bills apart by repeat mode and count only, so a food target and a stock
// target are one order, and a bill read back as a target cannot be told from a
// beer reserve nor a forever bill from a human butcher bill.
func (s OrderSpec) Key() string {
	ing := append([]string(nil), s.Ingredients...)
	sort.Strings(ing)
	return strings.Join([]string{s.BenchKind, s.Recipe, string(wireClass(s.Mode)), strconv.FormatInt(int64(s.Target), 10), s.Worker, strings.Join(ing, ",")}, "|")
}

// wireClass is the bill mode native reads back for m: one of GearBatch (count),
// ButcherForever (forever) and StockTarget (target).
func wireClass(m domain.BillMode) domain.BillMode {
	switch m {
	case domain.HumanButcherForever:
		return domain.ButcherForever
	case domain.FoodTarget, domain.BeerReserve:
		return domain.StockTarget
	}
	return m
}

// Declared is what one planner wants this Round. Abstain means the planner
// lacked facts, so its silence about a bill proves nothing.
type Declared struct {
	Orders  []OrderSpec
	Abstain bool
}

// LedgerBillKind classifies a bill on a bench. Everything but
// LedgerProduction is declare-only in the first pass and never removed.
type LedgerBillKind string

const (
	LedgerProduction    LedgerBillKind = ""
	LedgerMechGestation LedgerBillKind = "mech_gestation"
	LedgerSurgery       LedgerBillKind = "surgery"
	LedgerMedical       LedgerBillKind = "medical"
)

// ActualBill is a bill read back from a bench.
type ActualBill struct {
	// ID is the native bill identity and the key of the orphan counter.
	ID    string
	Bench string
	Kind  LedgerBillKind
	Spec  OrderSpec
	// Spent marks a finished bill: native does not count it as standing, so it
	// never satisfies a wanted spec and is an orphan like any other.
	Spent bool
	// Migrated is whether the journal says a migrated owner placed the bill
	// (LedgerMigratedOwner). Until the old bill machinery is deleted only such
	// a bill can be an orphan: any other is kept, uncounted.
	Migrated bool
}

// LedgerPlan is one Round's reconcile result. Orphans is the next Round's
// counter state, keyed by ActualBill.ID; callers keep it in memory.
type LedgerPlan struct {
	Place   []OrderSpec
	Remove  []ActualBill
	Keep    []ActualBill
	Orphans map[string]int
}

// ReconcileLedger diffs the declared orders against the actual bills. Identical
// declared specs coalesce into one wanted order. A wanted spec with no actual
// bill is placed. An actual bill matching a wanted spec is kept, once per
// spec; any further bill of that spec is an orphan, as is a bill no planner
// declared. If any planner abstained nothing is removed and the counters do
// not advance; otherwise an orphan is removed once it has been an orphan for
// OrphanGraceRounds consecutive Rounds. Excluded kinds are always kept.
// copies is the wanted bill count per spec Key (the dispatcher's split across
// benches); a spec absent from it, or below one, wants one bill.
func ReconcileLedger(declared []Declared, actual []ActualBill, orphans map[string]int, copies map[string]int) LedgerPlan {
	wanted, abstain := WantedOrders(declared)
	plan := LedgerPlan{Orphans: map[string]int{}}
	matched := map[string]int{}
	for _, b := range actual {
		k := b.Spec.Key()
		_, isWanted := wanted[k]
		switch {
		case b.Kind != LedgerProduction:
			plan.Keep = append(plan.Keep, b)
		case isWanted && matched[k] < wantedCopies(copies, k) && !b.Spent:
			matched[k]++
			plan.Keep = append(plan.Keep, b)
		case !b.Migrated:
			plan.Keep = append(plan.Keep, b)
		case abstain:
			plan.Keep = append(plan.Keep, b)
			if n, ok := orphans[b.ID]; ok {
				plan.Orphans[b.ID] = n
			}
		default:
			n := orphans[b.ID] + 1
			if n >= OrphanGraceRounds {
				plan.Remove = append(plan.Remove, b)
				continue
			}
			plan.Orphans[b.ID] = n
			plan.Keep = append(plan.Keep, b)
		}
	}
	keys := make([]string, 0, len(wanted))
	for k := range wanted {
		if matched[k] < wantedCopies(copies, k) {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	for _, k := range keys {
		for n := matched[k]; n < wantedCopies(copies, k); n++ {
			plan.Place = append(plan.Place, wanted[k])
		}
	}
	return plan
}

// WantedOrders coalesces the declared orders by spec Key (the first declaration
// of a key wins) and reports whether any planner abstained.
func WantedOrders(declared []Declared) (wanted map[string]OrderSpec, abstain bool) {
	wanted = map[string]OrderSpec{}
	for _, d := range declared {
		abstain = abstain || d.Abstain
		for _, o := range d.Orders {
			if _, ok := wanted[o.Key()]; !ok {
				wanted[o.Key()] = o
			}
		}
	}
	return wanted, abstain
}

// wantedCopies is how many bills of the spec stand, at least one.
func wantedCopies(copies map[string]int, key string) int {
	if n := copies[key]; n > 1 {
		return n
	}
	return 1
}
