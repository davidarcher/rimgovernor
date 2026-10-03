package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// OdysseyColony is the Odyssey colony section (#1709): active game
// conditions, hazardous terrain, lava emergences and underground sites. An
// absent scalar is unknown, never zero; the whole section is unknown without
// Odyssey or when the read failed.
type OdysseyColony struct {
	Conditions     []ActiveCondition
	HazardTerrain  []HazardTerrain
	LavaEmergences []LavaEmergence
	Sites          []UndergroundSite
}

// ActiveCondition is one GameCondition on the colony map. TicksLeft is
// unknown for a permanent condition.
type ActiveCondition struct {
	ID, Definition, Class  string
	Permanent              bool
	TicksPassed, TicksLeft domain.Fact[int32]
	CauserID               domain.Fact[string]
}

// HazardTerrain is a terrain def whose own flags hurt or contaminate and the
// cells it covers.
type HazardTerrain struct {
	Definition                      string
	Cells                           uint32
	Dangerous                       domain.Fact[bool]
	BurnDamage                      domain.Fact[int32]
	HeatPerTick, ToxicBuildupFactor domain.Fact[float64]
}

type LavaEmergence struct {
	ID       string
	Position domain.Cell
}

// UndergroundSite is an ancient hatch's pocket map. Hackable positions are
// cells of the pocket map, not the colony map.
type UndergroundSite struct {
	HatchID               string
	PocketMapID           int32
	StockpileType, Layout domain.Fact[string]
	ColonistsPresent      domain.Fact[uint32]
	Hackables             []UndergroundHackable
}

type UndergroundHackable struct {
	ID, Definition              string
	Position                    domain.Cell
	ProgressPercent, Defence    domain.Fact[float64]
	Hacked, LockedOut, Autohack domain.Fact[bool]
}

// colonyOdyssey projects a validated section; an absent or unavailable one is
// an unknown fact.
func colonyOdyssey(section *o.OdysseySection) domain.Fact[OdysseyColony] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[OdysseyColony]{}
	}
	var r OdysseyColony
	for _, x := range f.Conditions {
		r.Conditions = append(r.Conditions, ActiveCondition{ID: x.GetConditionId(), Definition: x.GetDefName(), Class: x.GetConditionClass(), Permanent: x.GetPermanent(),
			TicksPassed: optional(x.TicksPassed), TicksLeft: optional(x.TicksLeft), CauserID: optional(x.CauserId)})
	}
	for _, x := range f.HazardTerrain {
		r.HazardTerrain = append(r.HazardTerrain, HazardTerrain{Definition: x.GetDefName(), Cells: x.GetCells(), Dangerous: optional(x.Dangerous), BurnDamage: optional(x.BurnDamage),
			HeatPerTick: optional(x.HeatPerTick), ToxicBuildupFactor: optional(x.ToxicBuildupFactor)})
	}
	for _, x := range f.LavaEmergences {
		r.LavaEmergences = append(r.LavaEmergences, LavaEmergence{ID: x.GetThingId(), Position: domain.Cell{X: x.Position.GetX(), Z: x.Position.GetZ()}})
	}
	for _, x := range f.Sites {
		s := UndergroundSite{HatchID: x.GetHatchId(), PocketMapID: x.GetPocketMapId(), StockpileType: optional(x.StockpileType), Layout: optional(x.Layout), ColonistsPresent: optional(x.ColonistsPresent)}
		for _, h := range x.Hackables {
			s.Hackables = append(s.Hackables, UndergroundHackable{ID: h.GetThingId(), Definition: h.GetDefName(), Position: domain.Cell{X: h.Position.GetX(), Z: h.Position.GetZ()},
				ProgressPercent: optional(h.ProgressPercent), Defence: optional(h.Defence), Hacked: optional(h.Hacked), LockedOut: optional(h.LockedOut), Autohack: optional(h.Autohack)})
		}
		r.Sites = append(r.Sites, s)
	}
	return domain.Known(r)
}
