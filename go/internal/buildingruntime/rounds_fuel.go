package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// merge raises the memory's demand of snapshot's world by derived (the fuel
// runway, policy.PlanFuelRunway) without lowering any level it holds.
func (m *constructionMemory) merge(snapshot domain.GenerationSnapshot, derived map[policy.Resource]int64) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot == snapshot {
		m.needs = policy.ResourceConcernTargets(m.needs, derived)
	}
}
