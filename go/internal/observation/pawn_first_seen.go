package observation

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PawnFirstSeen is what the pawn table said of a humanlike pawn the first
// time it appeared in a world: when, and its faction, royal title and
// guest standing then. Later refreshes never change it.
type PawnFirstSeen struct {
	Tick        domain.Tick
	Faction     string // FactionDef defName; empty without a faction
	Title       string // most senior royal title defName; empty without one
	HostFaction string // holding faction's load id; empty when none
	Guest       string // GuestStatus name while a host faction holds the pawn
	QuestLodger bool
}

type firstSeenWorld struct {
	colony domain.ColonyID
	mapID  domain.MapID
	load   domain.LoadID
}

// FirstSeenRecord is the per-world first-seen record: derived, held in Go
// memory only and never saved. A pawn present at a reload looks new once. A
// world change (another colony, map or load) starts it empty.
type FirstSeenRecord struct {
	mu    sync.Mutex
	world firstSeenWorld
	seen  map[string]PawnFirstSeen
}

// Observe records every humanlike pawn of pawns not yet in id's world, at
// id.Tick, from its standing facts. A pawn already recorded is untouched.
func (r *FirstSeenRecord) Observe(id Identity, pawns bridge.Pawns) {
	world := firstSeenWorld{id.Colony, id.Map, id.Load}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.seen == nil || r.world != world {
		r.world, r.seen = world, map[string]PawnFirstSeen{}
	}
	for pawnID, row := range pawns.Map() {
		standing := row.GetStanding()
		if standing == nil || !row.GetHumanlike() {
			continue
		}
		if _, ok := r.seen[pawnID]; ok {
			continue
		}
		r.seen[pawnID] = PawnFirstSeen{Tick: id.Tick, Faction: standing.GetFactionDefName(), Title: standing.GetRoyalTitle(),
			HostFaction: standing.GetHostFaction().GetId(), Guest: standing.GetGuestStatus(), QuestLodger: standing.GetQuestLodger()}
	}
}

// Get is pawnID's first-seen facts in id's world, false when it has not been
// seen there.
func (r *FirstSeenRecord) Get(id Identity, pawnID string) (PawnFirstSeen, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.world != (firstSeenWorld{id.Colony, id.Map, id.Load}) {
		return PawnFirstSeen{}, false
	}
	seen, ok := r.seen[pawnID]
	return seen, ok
}
