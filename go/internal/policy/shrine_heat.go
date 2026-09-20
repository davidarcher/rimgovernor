package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
)

const ShrineHeatReadyC = 60.0
const ShrineHeatTargetC = 80.0

type ShrineHeater struct {
	ID   string
	Cell domain.Cell
}
type ShrineHeatFacts struct {
	Temperature, OutdoorTemperature                   float64
	Cells, BoundaryCells                              uint32
	Enclosed, ColonistsInside                         bool
	DoorSites, HeaterSites, FiringCells, RetreatCells []domain.Cell
	Heaters                                           []ShrineHeater
}

// ShrineHeaterCount estimates installation size; only a measured room >60 C
// admits opening. Each heater adds 21*250/60*eff energy per rare tick, divided
// by room cells and capped by the setpoint gap. Walls exchange 0.0204*W*dT
// every 120 ticks. Include enough net energy to gain 45 C over ten rare ticks.
func ShrineHeaterCount(f ShrineHeatFacts) int {
	if f.Cells == 0 || f.Cells > 256 || f.BoundaryCells == 0 || f.BoundaryCells > 1024 || math.IsNaN(f.OutdoorTemperature) || math.IsInf(f.OutdoorTemperature, 0) {
		return 0
	}
	loss := math.Max(0, 65-f.OutdoorTemperature) * float64(f.BoundaryCells) * 0.0204 * 250 / 120
	gain := float64(f.Cells) * 45 / 10
	return max(1, int(math.Ceil((loss+gain)/(21*250/60*0.55))))
}

type ShrineHeatProposal struct {
	Retreat domain.Cell
	Phase   string
	Cell    domain.Cell
	Pawn    domain.PawnID
	Casket  ShrineCasket
	Heaters int
}

func SelectShrineHeat(shrine AncientShrine, caskets []ShrineCasket, squad []ShrineDefenderFacts) ShrineHeatProposal {
	hold := func(reason string) ShrineHeatProposal { return ShrineHeatProposal{Phase: reason} }
	if shrine.Sealed || !shrine.InHome || !shrine.GuardsKnown || shrine.GuardsAlive() || len(caskets) == 0 {
		return hold("heat_ineligible")
	}
	f, known := shrine.Heat.Value()
	if !known || math.IsNaN(f.Temperature) || math.IsInf(f.Temperature, 0) {
		return hold("heat_unknown")
	}
	count := ShrineHeaterCount(f)
	if count == 0 || count > 8 {
		return hold("heat_capacity")
	}
	if len(f.DoorSites) == 1 {
		return ShrineHeatProposal{Phase: "heat_door", Cell: f.DoorSites[0]}
	}
	if !f.Enclosed || len(f.DoorSites) > 0 {
		return hold("heat_enclosure")
	}
	if len(f.FiringCells) == 0 || len(f.FiringCells) != len(f.RetreatCells) {
		return hold("heat_no_doorway")
	}
	if len(f.Heaters) < count {
		if len(f.HeaterSites) == 0 {
			return hold("heat_no_space")
		}
		return ShrineHeatProposal{Phase: "heat_heater", Cell: f.HeaterSites[0], Heaters: count}
	}
	if f.ColonistsInside {
		return hold("heat_colonists_inside")
	}
	pool := append([]ShrineDefenderFacts(nil), squad...)
	sort.Slice(pool, func(i, j int) bool { return pool[i].ID < pool[j].ID })
	for _, pawn := range pool {
		ranged, rk := pawn.RangedEquipped.Value()
		reach, known := pawn.WeaponRange.Value()
		if !shrineDefenderEligible(pawn) || !rk || !ranged || !known || math.IsNaN(reach) || math.IsInf(reach, 0) {
			continue
		}
		for _, casket := range caskets {
			if casket.MaxHitPoints == 0 || float64(casket.HitPoints)/float64(casket.MaxHitPoints) < 0.5 {
				continue
			}
			for i, cell := range f.FiringCells {
				dx, dz := float64(cell.X)-float64(casket.Cell.X), float64(cell.Z)-float64(casket.Cell.Z)
				if dx*dx+dz*dz > reach*reach {
					continue
				}
				phase := "heat_warming"
				if f.Temperature > ShrineHeatReadyC {
					phase = "heat_open"
				}
				return ShrineHeatProposal{Phase: phase, Cell: cell, Retreat: f.RetreatCells[i], Pawn: pawn.ID, Casket: casket, Heaters: count}
			}
		}
	}
	return hold("heat_no_shooter")
}
