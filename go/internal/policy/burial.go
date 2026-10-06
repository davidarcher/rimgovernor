package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainBurial (#2196, epic #2176) is the People department's burial: the
// tomb's sarcophagus or plain-grave step, the morgue's shell and the graveyard's
// fence and graves, and the tomb, morgue and graveyard stores. It is a Standard
// whose target is no human corpse owed a shell, a sarcophagus or a grave. Vanilla
// haulers carry the corpses; nothing here hauls.
const MaintainBurial ConcernID = "MaintainBurial"

// burialPriority ranks MaintainBurial with the other priority-3 chores.
const burialPriority = 3

// MorguePriority ranks the morgue below graves and sarcophagi, which take a
// colonist corpse the morgue holds until one has room, and above the Low corpse
// dump. The native acceptance case burial/grave_over_morgue verifies the ranking.
const MorguePriority = domain.NormalPriority

// inspectBurial raises MaintainBurial while a tomb, grave or morgue step is due.
func inspectBurial(c *roundsRun) error {
	recovered := domain.Unknown[bool]()
	if owed, known := c.f.BurialOwed.Value(); known {
		recovered = domain.Known(!owed)
	}
	c.assess(MaintainBurial, burialPriority, recovered)
	if !positive(recovered) {
		c.raise(MaintainBurial, burialPriority)
	}
	return nil
}

// BurialCensus is what the burial concern reads of the colony: the plan, the
// waste census, the colony's buildings and whether a sarcophagus can be had.
type BurialCensus struct {
	Waste       []WasteItem
	Built       []CurrentBuilding
	Shapes      PieceShapes
	Sarcophagus bool
}

// GraveyardsWanted is the graveyards the plan should hold: 0 for no demand,
// else the planned count plus one. A further graveyard is asked for only while
// no sarcophagus can be had (once one can, the tomb rooms take new burials) and
// when the empty graves run out: fewer free grave slots than the unburied dead
// still owed one, or the graveyards at StockpileFurtherRoomFill of their slots.
func GraveyardsWanted(plan LayoutPlan, waste []WasteItem, built []CurrentBuilding, shapes PieceShapes, sarcophagus bool) int {
	have := len(plan.roomsOf(PlannedGraveyard))
	if have == 0 || sarcophagus {
		return 0
	}
	standing, slots := GravesStanding(plan, built)
	step, _ := tombCensus(waste, built, shapes.Furniture.Sarcophagus)
	owed := max(step.Dead-step.Empty, 0)
	if owed > slots-standing || float64(standing) >= StockpileFurtherRoomFill*float64(slots) {
		return have + 1
	}
	return 0
}

// burialOwner is the People department's stores: the tomb and the morgue; the
// graveyard's graves are buildings and need no zone. The graveyard's room
// demand is the further graveyard (GraveyardsWanted).
type burialOwner struct{}

func (burialOwner) Department() Department { return DepartmentPeople }

func (burialOwner) Stores(v StorageRequest) []Store {
	if v.Layout == nil {
		return nil
	}
	var out []Store
	for _, s := range []struct {
		role     PlannedRole
		prefix   string
		filter   domain.StockpileFilter
		priority domain.StockpilePriority
	}{
		{PlannedTomb, domain.TombRolePrefix, domain.TombCorpsesFilter(), domain.CriticalPriority},
		{PlannedMorgue, domain.MorgueRolePrefix, domain.MorgueCorpsesFilter(), MorguePriority},
	} {
		// The first planned room of the role holds the store.
		for _, room := range v.Layout.roomsOf(s.role) {
			key := fmt.Sprintf("%d_%d", room.Interior.X, room.Interior.Z)
			out = append(out, Store{StoreSite: StoreSite{Role: s.prefix + key, Interior: room.Interior, Filter: s.filter, Priority: s.priority}})
			break
		}
	}
	return out
}

func (burialOwner) RoomDemand(v StorageRequest) RoomDemand {
	if v.Layout == nil || v.Burial == nil {
		return RoomDemand{}
	}
	b := v.Burial
	return RoomDemand{Graveyards: GraveyardsWanted(*v.Layout, b.Waste, b.Built, b.Shapes, b.Sarcophagus)}
}
