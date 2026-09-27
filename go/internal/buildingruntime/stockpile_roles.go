package buildingruntime

import (
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// StockpileRoleSource publishes the desired state of the stockpile roles it
// owns (#725): given the review's projection and a full role key (for
// example "ingredients:Bench_12"), the filter and priority the role's zones
// should carry, or Retired once its purpose is gone. False leaves the
// role's zones as they are. A source must be deterministic over the
// projection and cheap: MaintainStockpiles calls it every review cycle.
type StockpileRoleSource func(projection *observation.ColonyProjection, role string) (policy.StockpileRoleState, bool)

var stockpileRoleRegistry = struct {
	sync.RWMutex
	sources map[string]StockpileRoleSource
}{sources: map[string]StockpileRoleSource{}}

// RegisterStockpileRole makes source the owner of every role whose key is
// prefix or starts with prefix+":" (the role planners #721/#723/#724
// register theirs from an init function in their own files). A prefix has
// one owner; registering it twice panics.
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

// stockpileRoles binds the registered sources to one projection.
func stockpileRoles(projection *observation.ColonyProjection) policy.StockpileRoles {
	return func(role string) (policy.StockpileRoleState, bool) {
		prefix, _, _ := strings.Cut(role, ":")
		stockpileRoleRegistry.RLock()
		source := stockpileRoleRegistry.sources[prefix]
		stockpileRoleRegistry.RUnlock()
		if source == nil {
			return policy.StockpileRoleState{}, false
		}
		return source(projection, role)
	}
}
