package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainIncineration is the Sanitation department's incinerator concern: it
// shells the waste yard and the incinerator inside it, burns a full incinerator
// and cleans the ash (rounds_incineration*.go). It is a Standard whose target
// is no shell, burn or ash owed. Its zone is a declared Sanitation store
// (incinerationOwner), applied by MaintainStockpiles.
const MaintainIncineration ConcernID = "MaintainIncineration"

// inspectIncineration: unknown without the plan and construction census, and
// then it raises no goal.
func inspectIncineration(c *roundsRun) error {
	c.assess(MaintainIncineration, 3, notFact(c.f.IncinerationOwed))
	if owed, known := c.f.IncinerationOwed.Value(); known && owed {
		c.raise(MaintainIncineration, 3)
	}
	return nil
}

// incinerationOwner is the Sanitation department's stores: the incinerator
// zone once its walls stand.
type incinerationOwner struct{}

func (incinerationOwner) Department() Department { return DepartmentSanitation }

// Stores: the incinerator's whole interior at Preferred, above the Low dump so
// waste hauls in from it, taking what the native rule calls burnable.
func (incinerationOwner) Stores(v StorageRequest) []Store {
	if v.Incinerator == nil {
		return nil
	}
	return []Store{{StoreSite: StoreSite{Role: domain.IncineratorRole, Interior: v.Incinerator.Interior, Filter: domain.IncineratorFilter(), Priority: domain.PreferredPriority, exact: true}}}
}

func (o incinerationOwner) RoomDemand(v StorageRequest) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}
