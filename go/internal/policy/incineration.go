package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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

// incinerationOwner is the Sanitation department's stores: the waste yard's one
// dump zone from plan time, and the incinerator zone once its walls stand.
type incinerationOwner struct{}

func (incinerationOwner) Department() Department { return DepartmentSanitation }

// Stores: the dump covers the waste yard interior outside the incinerator
// room's outline at Low priority, taking all storable items but the native
// not-burnable ones. The incinerator's whole interior is Preferred, above the
// dump so waste hauls in from it, taking what the native rule calls burnable.
func (incinerationOwner) Stores(v StoreView) []Store {
	var out []Store
	if dump, ok := wasteDumpSite(v.Layout); ok {
		out = append(out, Store{StoreSite: dump})
	}
	if v.Incinerator != nil {
		out = append(out, Store{StoreSite: StoreSite{Role: domain.IncineratorRole, Interior: v.Incinerator.Interior, Filter: domain.IncineratorFilter(), Priority: domain.PreferredPriority, exact: true}})
	}
	return out
}

// wasteDumpSite is the dump's site: the first planned waste yard's interior
// less the planned incinerator's outline (its walls and interior). It reads
// the plan, so the zone stands before the yard is fenced.
func wasteDumpSite(plan *LayoutPlan) (StoreSite, bool) {
	if plan == nil {
		return StoreSite{}, false
	}
	yards := plan.roomsOf(PlannedWasteYard)
	if len(yards) == 0 {
		return StoreSite{}, false
	}
	yard := yards[0].Interior
	cells := rectCells(yard)
	for _, inc := range plan.roomsOf(PlannedIncinerator) {
		outline := cellSet(rectCells(pad(inc.Interior, 1)))
		cells = slices.DeleteFunc(cells, func(c domain.Cell) bool { return outline[c] })
	}
	return StoreSite{Role: domain.DumpRole, Interior: yard, Filter: domain.DumpFilter(), Priority: domain.LowPriority, room: cells, exact: true}, true
}

func (o incinerationOwner) RoomDemand(v StoreView) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}
