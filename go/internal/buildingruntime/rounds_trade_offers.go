package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// tradeOfferBook is the priced offers of the caravans the negotiator has
// looked at, in Go memory only (derived state): RoundsTradePlanner records one
// at each sheet read, the Round's supply plan reads the fresh ones as trade
// candidates, and a record is dropped when its trader leaves the census or
// the world changes. The food plan reads the same records to present traders
// as trade candidates (#2166).
type tradeOfferBook struct {
	mu       sync.Mutex
	snapshot domain.GenerationSnapshot
	records  map[string]policy.TradeOffers
	// revision counts the records and drops, so a plan built before one is
	// rebuilt after it.
	revision uint64
}

// sameOfferWorld is whether two snapshots are one loaded world: a caravan's
// offers outlive plan revisions and native generations, not a load or map.
func sameOfferWorld(a, b domain.GenerationSnapshot) bool {
	return a.Colony == b.Colony && a.Load == b.Load && a.Map == b.Map
}

// record replaces the trader's offers.
func (b *tradeOfferBook) record(snapshot domain.GenerationSnapshot, offers policy.TradeOffers) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !sameOfferWorld(b.snapshot, snapshot) || b.records == nil {
		b.snapshot, b.records = snapshot, map[string]policy.TradeOffers{}
	}
	b.records[offers.Trader] = offers
	b.revision++
}

// fresh are the records of the traders on the census that can trade and
// whose record still describes them (policy.TradeOffersFresh), in trader id
// order. A trader that left the census loses its record.
func (b *tradeOfferBook) fresh(snapshot domain.GenerationSnapshot, now domain.Tick, silver int64, traders []policy.TraderFacts) []policy.TradeOffers {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !sameOfferWorld(b.snapshot, snapshot) {
		return nil
	}
	present := map[string]policy.TraderFacts{}
	for _, t := range traders {
		present[t.ID] = t
	}
	var out []policy.TradeOffers
	for id, record := range b.records {
		t, ok := present[id]
		if !ok {
			delete(b.records, id)
			b.revision++
			continue
		}
		if t.CanTrade && policy.TradeOffersFresh(record, now, silver, t.GoodsStacks) {
			out = append(out, record)
		}
	}
	slices.SortFunc(out, func(a, c policy.TradeOffers) int {
		switch {
		case a.Trader < c.Trader:
			return -1
		case a.Trader > c.Trader:
			return 1
		}
		return 0
	})
	return out
}

func (b *tradeOfferBook) version() uint64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.revision
}

func (b *tradeOfferBook) drop(trader string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if _, ok := b.records[trader]; ok {
		delete(b.records, trader)
		b.revision++
	}
}

// recordOffers books what the open session's sheet prices: every non-currency
// row the trader holds at a known price, with the colony's silver and the
// trader's goods stacks the record's freshness is judged against. The plan
// reads it from the next plan build on (the book's revision keys the cache).
func (r *RoundsTradePlanner) recordOffers(call context.Context, state ControlState, sheet bridge.TradeSheetRead, stacks int64) error {
	silver, _, silverKnown := tradeSheetSilver(sheet.Rows)
	if !silverKnown {
		return nil
	}
	tables, err := r.native.FrameTables(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return err
	}
	if tables.Catalog == nil {
		return fmt.Errorf("%w: recordOffers: no definition catalog", ErrControl)
	}
	offers := policy.TradeOffers{Trader: sheet.Trader, Negotiator: sheet.Negotiator, Tick: domain.Tick(sheet.Context.GetTick()), Silver: silver, GoodsStacks: stacks}
	for _, row := range sheet.Rows {
		if row.CurrencyKnown && row.Currency || row.TraderCount <= 0 || !row.BuyPriceKnown {
			continue
		}
		food, err := tradeFoodFact(row.Food, row.DefName, tables.Catalog)
		if err != nil {
			return err
		}
		offers.Rows = append(offers.Rows, policy.TradeOffer{Def: row.DefName, Count: row.TraderCount, Price: row.BuyPrice, Food: food})
	}
	r.reviewer.tradeOffers.record(state.Snapshot, offers)
	return nil
}

// plannedPurchases is the units of each resource the Round's supply plan
// opened to buy from trader, empty when MaintainResource has no work.
func (r *RoundsTradePlanner) plannedPurchases(call context.Context, state ControlState, review store.Rounds, trader string) (map[policy.Resource]int64, error) {
	goal, workable, err := r.reviewer.player.journal.Workable(call, review, policy.MaintainResource)
	if err != nil || !workable {
		return nil, err
	}
	supply, err := r.reviewer.resourceSupply(call, state, review, goal)
	if err != nil {
		return nil, err
	}
	return supply.tradeLines(trader), nil
}

// restrictToPlan keeps the resource purchases of need (a MaintainResource
// shortfall, the component target) and its food nutrition (what the food plan
// opened to buy from this trader) only as far as the plans opened them;
// medicine, surgery parts, ingredient upgrades and every sale stay.
func restrictToPlan(need policy.TradeNeed, planned map[policy.Resource]int64, food float64) policy.TradeNeed {
	need.Food.Nutrition, need.Food.Browse = food, false
	var kept []policy.Amount
	for _, short := range need.Shortfall {
		if units := min(short.Count, planned[short.Resource]); units > 0 {
			kept = append(kept, policy.Amount{Resource: short.Resource, Count: units})
		}
	}
	need.Shortfall = kept
	need.ComponentShortfall = min(need.ComponentShortfall, planned[policy.ComponentResource])
	return need
}
