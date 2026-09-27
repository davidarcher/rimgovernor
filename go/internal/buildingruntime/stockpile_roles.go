package buildingruntime

import (
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// StockpileRoleInput is what a role source judges its roles on: the
// review's projection and the bill-giving benches standing now (the bench
// census; unknown when unread or when no owned role needs it).
type StockpileRoleInput struct {
	Projection *observation.ColonyProjection
	Benches    domain.Fact[map[string]bool]
}

// StockpileRoleSource publishes the desired state of the stockpile roles it
// owns (#725): given the review's input and a full role key (for example
// "ingredients:Bench_12"), the filter and priority the role's zones should
// carry, or Retired once its purpose is gone. False leaves the role's zones
// as they are, and a source answers false whenever the fact it judges by is
// unknown. A source must be deterministic over its input and cheap:
// MaintainStockpiles calls it every review cycle.
type StockpileRoleSource func(in StockpileRoleInput, role string) (policy.StockpileRoleState, bool)

var stockpileRoleRegistry = struct {
	sync.RWMutex
	sources map[string]StockpileRoleSource
}{sources: map[string]StockpileRoleSource{}}

// RegisterStockpileRole makes source the owner of every role whose key is
// prefix or starts with prefix+":" (each role's owner registers from an
// init function in its own file). A prefix has one owner; registering it
// twice panics.
func RegisterStockpileRole(prefix string, source StockpileRoleSource) {
	if prefix == "" || strings.Contains(prefix, ":") || source == nil {
		panic("invalid stockpile role registration")
	}
	stockpileRoleRegistry.Lock()
	defer stockpileRoleRegistry.Unlock()
	if _, taken := stockpileRoleRegistry.sources[prefix]; taken {
		panic("stockpile role registered twice: " + prefix)
	}
	stockpileRoleRegistry.sources[prefix] = source
}

// stockpileRoles binds the registered sources to one input.
func stockpileRoles(in StockpileRoleInput) policy.StockpileRoles {
	return func(role string) (policy.StockpileRoleState, bool) {
		prefix, _, _ := strings.Cut(role, ":")
		stockpileRoleRegistry.RLock()
		source := stockpileRoleRegistry.sources[prefix]
		stockpileRoleRegistry.RUnlock()
		if source == nil {
			return policy.StockpileRoleState{}, false
		}
		return source(in, role)
	}
}

// fixedStockpileRole publishes one fixed filter and priority for a role
// that never retires.
func fixedStockpileRole(filter domain.StockpileFilter, priority domain.StockpilePriority) StockpileRoleSource {
	return func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{Filter: filter, Priority: priority}, true
	}
}
