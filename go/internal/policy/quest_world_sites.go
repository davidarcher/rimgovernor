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
	Security       domain.Fact[SiteSecurity]
	Extraction     domain.Fact[SiteExtraction]
	PeaceTalks     domain.Fact[PeaceTalksRisk]
	MiningTargets  []SiteMiningTarget
}
type SiteMiningTarget struct {
	ID, Def    string
	Cell       domain.Cell
	Map        domain.MapID
	Designated domain.Fact[bool]
}

type SiteSecurity struct {
	Known, ActiveThreat, DormantThreat, DetectionActive domain.Fact[bool]
	InitialPoints, PendingRaidPoints                    domain.Fact[float64]
	TrapCount, RaidsSent                                domain.Fact[int32]
	DetectionTicksLeft                                  domain.Fact[int64]
}
type SiteExtraction struct {
	CanReform                                     domain.Fact[bool]
	Crew                                          []domain.PawnID
	CarryCapacity, CarriedMass, InventoryFoodDays domain.Fact[float64]
	CrewNutritionPerDay                           domain.Fact[float64]
	Cargo                                         []SiteCargo
	HomeRoutes                                    []SiteHomeRoute
	ExitCells                                     []domain.Cell
}
type SiteCargo struct {
	ID, Def                          string
	Count                            domain.Fact[int64]
	UnitMass, MarketValue, Nutrition domain.Fact[float64]
	Held                             domain.Fact[bool]
}
type SiteHomeRoute struct {
	Map         domain.MapID
	Tile        int32
	Reachable   domain.Fact[bool]
	TravelTicks domain.Fact[int64]
}
type QuestSurveyScanner struct {
	SiteID, ScannerID                string
	DurationTicks, EndTick, RaidTick domain.Fact[int64]
	Alive, Complete                  domain.Fact[bool]
}

type QuestGravEngine struct {
	ID                               string
	Map                              domain.MapID
	Spawned, Inspected               domain.Fact[bool]
	Cell                             domain.Cell
	EligiblePawns, InspectingPawnIDs []domain.PawnID
}
