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
}

// Key is the canonical spec identity.
func (s OrderSpec) Key() string {
	ing := append([]string(nil), s.Ingredients...)
	sort.Strings(ing)
	return strings.Join([]string{s.BenchKind, s.Recipe, string(s.Mode), strconv.FormatInt(int64(s.Target), 10), s.Worker, strings.Join(ing, ",")}, "|")
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
func ReconcileLedger(declared []Declared, actual []ActualBill, orphans map[string]int) LedgerPlan {
	wanted := map[string]OrderSpec{}
	abstain := false
	for _, d := range declared {
		abstain = abstain || d.Abstain
		for _, o := range d.Orders {
			if _, ok := wanted[o.Key()]; !ok {
				wanted[o.Key()] = o
			}
		}
	}
	plan := LedgerPlan{Orphans: map[string]int{}}
	matched := map[string]bool{}
	for _, b := range actual {
		k := b.Spec.Key()
		_, isWanted := wanted[k]
		switch {
		case b.Kind != LedgerProduction:
			plan.Keep = append(plan.Keep, b)
		case isWanted && !matched[k]:
			matched[k] = true
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
		if !matched[k] {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	for _, k := range keys {
		plan.Place = append(plan.Place, wanted[k])
	}
	return plan
}
