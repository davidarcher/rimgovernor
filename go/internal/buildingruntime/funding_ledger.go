package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/policy"

// fundingLedger is one stock reading and what a batch of building admissions
// has claimed against it. Several units admitted in one pass (a wing's rooms,
// a wall's sections) are funded from it in order: a unit is admitted only while
// the stock still pays its costs after what the pass already claimed. The
// stock is the only limit; the ledger caps nothing else.
type fundingLedger struct {
	stock  policy.StockObservation
	spend  map[policy.Resource]int64
	priced map[string][]policy.Amount
}

func newFundingLedger(stock policy.StockObservation) *fundingLedger {
	return &fundingLedger{stock: stock, spend: map[policy.Resource]int64{}, priced: map[string][]policy.Amount{}}
}

// merge folds one preview's stock read into the ledger's reading; first is
// whether it is the ledger's first read.
func (l *fundingLedger) merge(next policy.StockObservation, first bool) error {
	return mergeRoundsStock(&l.stock, next, first)
}

// funded is whether the stock read so far still pays costs after what is
// claimed; a resource the stock does not name is not funded.
func (l *fundingLedger) funded(costs []policy.Amount) bool {
	need := map[policy.Resource]int64{}
	for _, c := range costs {
		need[c.Resource] += c.Count
	}
	for resource, count := range need {
		available, known := int64(0), false
		for _, s := range l.stock.Values {
			if v, ok := s.Available.Value(); ok && s.Resource == resource {
				available, known = v, true
			}
		}
		if !known || l.spend[resource]+count > available {
			return false
		}
	}
	return true
}

// claim charges costs to the ledger.
func (l *fundingLedger) claim(costs []policy.Amount) {
	for _, c := range costs {
		l.spend[c.Resource] += c.Count
	}
}

// price records what one building kind (definition and stuff) costs.
func (l *fundingLedger) price(kind string, costs []policy.Amount) { l.priced[kind] = costs }

// priceOf is the recorded cost of a building kind.
func (l *fundingLedger) priceOf(kind string) ([]policy.Amount, bool) {
	costs, seen := l.priced[kind]
	return costs, seen
}
