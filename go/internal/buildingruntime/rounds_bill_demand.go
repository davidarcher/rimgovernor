package buildingruntime

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// billAges is the tick each undispatched gear-batch bill was first seen by a
// review: derived state the Rounder keeps in memory, so a restart restarts
// every bill's expiry clock.
type billAges struct {
	mu   sync.Mutex
	seen map[domain.ActionID]domain.Tick
}

// age records the open undispatched bills and returns those first seen more
// than policy.OpenBillExpiry before now. A bill no longer open is forgotten.
func (a *billAges) age(open []domain.ActionID, now domain.Tick) []domain.ActionID {
	a.mu.Lock()
	defer a.mu.Unlock()
	next := make(map[domain.ActionID]domain.Tick, len(open))
	var expired []domain.ActionID
	for _, id := range open {
		first, known := a.seen[id]
		if !known {
			first = now
		}
		if policy.OpenBillExpired(first, now) {
			expired = append(expired, id)
			continue
		}
		next[id] = first
	}
	a.seen = next
	return expired
}

// cancelExpired cancels the gear-batch bills that sat undispatched for
// longer than policy.OpenBillExpiry (the need that placed one may be gone;
// native keeps no stock check and a still-wanted bill is placed again),
// replacing the cancelled progress in plans so the demand no longer counts
// it. A dispatched bill is a native bill the journal cannot withdraw.
func (a *billAges) cancelExpired(ctx context.Context, journal *store.Store, plans []store.PlanState, now domain.Tick) error {
	var open []domain.ActionID
	where := map[domain.ActionID][2]int{}
	for i, plan := range plans {
		if plan.Retired {
			continue
		}
		for j, progress := range plan.Progress {
			v := progress.View()
			if bill, ok := progress.Action().ProductionBill(); ok && bill.Mode() == domain.GearBatch && v.Attempt == 0 && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
				open = append(open, v.Action)
				where[v.Action] = [2]int{i, j}
			}
		}
	}
	for _, id := range a.age(open, now) {
		i, j := where[id][0], where[id][1]
		cancelled, err := journal.Cancel(ctx, plans[i].Spec.ID(), id)
		if err != nil {
			return err
		}
		plans[i].Progress[j] = cancelled
	}
	return nil
}

// openBills are the open gear-batch bills (Forever and stock-target bills are
// not counted) with their catalog slot counts read per recipe:
// ResourceDemandOf turns them into ingredient demand. A migrated planner's
// bills are the ledger's declared batches (an order stands from its
// declaration, placed or not); every other owner's are the journal's open
// bills. Expired bills are cancelled first. It is empty when the reviewer's
// source serves no definitions.
func (r *Rounder) openBills(ctx context.Context, snapshot domain.GenerationSnapshot, now domain.Tick, plans []store.PlanState) ([]policy.OpenBill, error) {
	if err := r.billAges.cancelExpired(ctx, r.player.journal, plans, now); err != nil {
		return nil, err
	}
	source, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return nil, nil
	}
	var bills []policy.OpenBill
	for _, order := range r.ledger.declaredBatches() {
		bills = append(bills, policy.OpenBill{Recipe: order.Recipe, Count: order.Target, Filter: order.Ingredients})
	}
	for _, plan := range plans {
		// The ledger's plans place what its batches above already count.
		if plan.Retired || ledgerPlanMethod(plan.Method) {
			continue
		}
		for _, progress := range plan.Progress {
			if bill, ok := progress.Action().ProductionBill(); ok && bill.Mode() == domain.GearBatch && domain.StandardWorkOpen([]domain.Progress{progress}) {
				bills = append(bills, policy.OpenBill{Recipe: bill.Recipe(), Count: bill.Target(), Filter: bill.Ingredients()})
			}
		}
	}
	if len(bills) == 0 {
		return nil, nil
	}
	catalog, err := source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
	if err != nil {
		return nil, err
	}
	for i := range bills {
		// A recipe the catalog cannot describe leaves its slots unknown.
		if bills[i].Slots, err = catalog.RecipeIngredients(bills[i].Recipe); err != nil {
			bills[i].Slots = domain.Unknown[[][]policy.Amount]()
		}
	}
	return bills, nil
}
