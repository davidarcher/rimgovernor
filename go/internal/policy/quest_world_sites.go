package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type WorldSite struct {
	ID, Def, Label string
	Tile, Layer    domain.Fact[int32]
	State          o.WorldSiteState
	Map            domain.Fact[domain.MapID]
	DistanceTiles  domain.Fact[float64]
	QuestIDs       []domain.QuestID
	Threat         domain.Fact[bool]
	ThreatPoints   domain.Fact[float64]
	Reachable      domain.Fact[bool]
	TravelTicks    domain.Fact[int64]
	RoutePawnIDs   []domain.PawnID
}

type QuestGravEngine struct {
	ID                               string
	Map                              domain.MapID
	Spawned, Inspected               domain.Fact[bool]
	Cell                             domain.Cell
	EligiblePawns, InspectingPawnIDs []domain.PawnID
}
