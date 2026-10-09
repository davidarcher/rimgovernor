package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// resourceDemandMemory is the resource demand the latest review of one world
// derived (policy.ResourceDemandOf, as the detectors read it): derived state
// the Rounder keeps in memory, empty until the first review after a restart.
// Every planner and the work coverage read it.
type resourceDemandMemory struct {
	mu       sync.Mutex
	snapshot domain.GenerationSnapshot
	demand   policy.DerivedDemand
}

func (m *resourceDemandMemory) set(snapshot domain.GenerationSnapshot, demand policy.DerivedDemand) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.snapshot, m.demand = snapshot, demand
}

// get is the demand the review of snapshot's world derived, none for another
// world.
func (m *resourceDemandMemory) get(snapshot domain.GenerationSnapshot) policy.DerivedDemand {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot {
		return policy.DerivedDemand{}
	}
	return m.demand
}

// resourceTargets is the stock targets every resource planner dispatches on:
// the review's demand.
func (r *Rounder) resourceTargets(snapshot domain.GenerationSnapshot) map[policy.Resource]int64 {
	return r.demand.get(snapshot).Needs
}
