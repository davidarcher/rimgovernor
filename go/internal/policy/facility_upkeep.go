package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	MaintainHomeCoverage GoalID = "MaintainHomeCoverage"
	MaintainStoneShell   GoalID = "MaintainStoneShell"
)

type OwnedStockpile struct {
	ID    string
	Cells []domain.Cell
}
type HomeCoverageTarget struct {
	ID                string
	Shape             domain.Fact[string]
	Missing, Excluded domain.Fact[int64]
	Cells             []domain.Cell
	Blocker           string
}
type HomeCoverageObservation struct {
	Revision int64
	Targets  []HomeCoverageTarget
}
type StoneStructure struct {
	ID           string
	Definition   Resource
	Flammability domain.Fact[float64]
}

func facilityCells(cells []domain.Cell) bool {
	if len(cells) < 1 || len(cells) > 256 {
		return false
	}
	seen := map[domain.Cell]bool{}
	for _, c := range cells {
		if c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || seen[c] {
			return false
		}
		seen[c] = true
	}
	return true
}

func ReviewHomeCoverage(owned domain.Fact[[]ConstructionClaim], zones domain.Fact[[]OwnedStockpile], observed domain.Fact[HomeCoverageObservation]) (domain.Fact[[]HomeCoverageTarget], error) {
	unknown := domain.Unknown[[]HomeCoverageTarget]()
	buildings, bk := owned.Value()
	stockpiles, zk := zones.Value()
	if !bk || !zk {
		return unknown, nil
	}
	if len(buildings) > 256 || len(stockpiles) > 256 {
		return unknown, errors.New("owned facilities exceed bound")
	}
	ids := map[string]bool{}
	footprints := map[string]map[domain.Cell]bool{}
	for _, b := range buildings {
		if !foodID(b.Identity.Current) || ids[b.Identity.Current] {
			return unknown, errors.New("invalid owned building")
		}
		ids[b.Identity.Current] = true
	}
	for _, z := range stockpiles {
		if !foodID(z.ID) || ids[z.ID] || !facilityCells(z.Cells) {
			return unknown, errors.New("invalid owned stockpile")
		}
		ids[z.ID] = true
		footprints[z.ID] = map[domain.Cell]bool{}
		for _, c := range z.Cells {
			footprints[z.ID][c] = true
		}
	}
	if len(ids) == 0 {
		return domain.Known([]HomeCoverageTarget{}), nil
	}
	census, known := observed.Value()
	if !known {
		return unknown, nil
	}
	if census.Revision < 0 || len(census.Targets) > 256 {
		return unknown, errors.New("invalid Home census")
	}
	result := []HomeCoverageTarget{}
	seen := map[string]bool{}
	for _, row := range census.Targets {
		if !foodID(row.ID) || seen[row.ID] {
			return unknown, errors.New("invalid Home target identity")
		}
		seen[row.ID] = true
		if !ids[row.ID] {
			continue
		}
		missing, mk := row.Missing.Value()
		excluded, ek := row.Excluded.Value()
		shape, sk := row.Shape.Value()
		if !mk || !ek || !sk || !facilityCells(row.Cells) {
			return unknown, nil
		}
		if !foodID(shape) || excluded < 0 || excluded > missing || missing > int64(len(row.Cells)) {
			return unknown, errors.New("invalid Home target geometry or counts")
		}
		if original, zone := footprints[row.ID]; zone {
			changed := len(original) != len(row.Cells)
			for _, cell := range row.Cells {
				changed = changed || !original[cell]
			}
			if changed {
				row.Blocker = "Owned stockpile geometry changed; preserve player edits"
			}
		}
		if missing > 0 || row.Blocker != "" {
			row.Cells = append([]domain.Cell{}, row.Cells...)
			result = append(result, row)
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return domain.Known(result), nil
}

func ReviewStoneShell(owned domain.Fact[[]ConstructionClaim], structures domain.Fact[[]StoneStructure]) (domain.Fact[[]string], error) {
	unknown := domain.Unknown[[]string]()
	buildings, known := owned.Value()
	if !known {
		return unknown, nil
	}
	if len(buildings) > 256 {
		return unknown, errors.New("owned construction exceeds bound")
	}
	walls := map[string]bool{}
	for _, b := range buildings {
		if b.Building.Definition() == "Wall" {
			if !foodID(b.Identity.Current) || walls[b.Identity.Current] {
				return unknown, errors.New("invalid owned wall")
			}
			walls[b.Identity.Current] = true
		}
	}
	if len(walls) == 0 {
		return domain.Known([]string{}), nil
	}
	rows, known := structures.Value()
	if !known {
		return unknown, nil
	}
	if len(rows) > 256 {
		return unknown, errors.New("structure census exceeds bound")
	}
	seen := map[string]bool{}
	result := []string{}
	for _, row := range rows {
		if !foodID(row.ID) || seen[row.ID] || !validResource(row.Definition) {
			return unknown, errors.New("invalid structure census")
		}
		seen[row.ID] = true
		if !walls[row.ID] {
			continue
		}
		flam, known := row.Flammability.Value()
		if !known || row.Definition != "Wall" {
			return unknown, nil
		}
		if !foodNumber(flam) {
			return unknown, errors.New("invalid wall flammability")
		}
		if flam > 0 {
			result = append(result, row.ID)
		}
	}
	// A current owned wall missing from the same-tick structure census is a
	// conflicting observation, not evidence that its stone upgrade recovered.
	for id := range walls {
		if !seen[id] {
			return unknown, nil
		}
	}
	sort.Strings(result)
	return domain.Known(result), nil
}
