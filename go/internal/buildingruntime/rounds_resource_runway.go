package buildingruntime

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type resourceRunwaySource interface {
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
}

// consumptionSource is native's saved realized-consumption ring.
type consumptionSource interface {
	ReadConsumption(ctx context.Context, sinceHour int) (policy.ConsumptionPage, error)
}

// consumptionMemory is Go's in-memory copy of the ring for one load. A new
// load (or a restart, which empties it) asks native for the whole window once.
type consumptionMemory struct {
	mu     sync.Mutex
	colony domain.ColonyID
	load   domain.LoadID
	ledger policy.ConsumptionLedger
}

// resourceConsumption brings the ledger up to the last completed hour and
// returns the recurring spend over the rate window. A failed read is unknown.
func (r *Rounder) resourceConsumption(ctx context.Context, snapshot domain.GenerationSnapshot) domain.Fact[policy.ResourceConsumption] {
	reader, ok := r.native.(consumptionSource)
	if !ok {
		return domain.Unknown[policy.ResourceConsumption]()
	}
	m := &r.consumption
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.colony != snapshot.Colony || m.load != snapshot.Load {
		m.colony, m.load, m.ledger = snapshot.Colony, snapshot.Load, policy.ConsumptionLedger{}
	}
	page, err := reader.ReadConsumption(ctx, m.ledger.Since())
	if err != nil {
		return domain.Unknown[policy.ResourceConsumption]()
	}
	m.ledger.Merge(page)
	return m.ledger.Window(policy.ResourceRateWindowDays)
}

// resourceSurfaceOre reads the safe surface ore of every resource the
// consumption ring shows recurring spend on.
func (r *Rounder) resourceSurfaceOre(ctx context.Context, snapshot domain.GenerationSnapshot, items policy.ItemFacts, consumption domain.Fact[policy.ResourceConsumption]) map[policy.Resource]domain.Fact[int64] {
	out := map[policy.Resource]domain.Fact[int64]{}
	reader, ok := r.native.(resourceRunwaySource)
	if !ok {
		return out
	}
	for _, resource := range policy.MaterialRunwayKeys(items, consumption) {
		rows, _, _, err := reader.ReadResourceSources(ctx, boundary.Identity(snapshot), string(resource))
		if err == nil {
			out[resource] = policy.SurfaceOre(rows)
		}
	}
	return out
}
