package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	MaintainHomeCoverage ConcernID = "MaintainHomeCoverage"
	MaintainStoneShell   ConcernID = "MaintainStoneShell"
)

type HomeCoverageTarget struct {
	// ExtentGeometry is complete, unbatched geometry. Legacy Home batches
	// leave it unknown and cannot establish colony territory.
	ExtentGeometry    domain.Fact[HomeExtentGeometry]
	ID                string
	Shape             domain.Fact[string]
	Missing, Excluded domain.Fact[int64]
	Cells             []domain.Cell
	Blocker           string
}
type HomeCoverageObservation struct {
	Revision int64
	Targets  []HomeCoverageTarget
	// Home is every current home-area cell; AutoHome the game's
	// auto-expand setting (#1328).
	Home     domain.Fact[[]domain.Cell]
	AutoHome domain.Fact[bool]
}
type StoneStructure struct {
	ID           string
	Definition   Resource
	Flammability domain.Fact[float64]
}

func facilityCells(cells []domain.Cell) bool {
	if len(cells) < 1 {
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

// ReviewHomeCoverage lists every census target (all colonist buildings and
// every stockpile, #719: the autopilot owns them whoever built them) still
// missing Home, at its current geometry.
func ReviewHomeCoverage(observed domain.Fact[HomeCoverageObservation]) (domain.Fact[[]HomeCoverageTarget], error) {
	unknown := domain.Unknown[[]HomeCoverageTarget]()
	census, known := observed.Value()
	if !known {
		return unknown, nil
	}
	if census.Revision < 0 {
		return unknown, errors.New("invalid Home census")
	}
	result := []HomeCoverageTarget{}
	seen := map[string]bool{}
	for _, row := range census.Targets {
		if !foodID(row.ID) || seen[row.ID] {
			return unknown, errors.New("invalid Home target identity")
		}
		seen[row.ID] = true
		missing, mk := row.Missing.Value()
		excluded, ek := row.Excluded.Value()
		shape, sk := row.Shape.Value()
		if !mk || !ek || !sk || !facilityCells(row.Cells) {
			return unknown, nil
		}
		if !foodID(shape) || excluded < 0 || excluded > missing || missing < 0 || missing > int64(len(row.Cells)) {
			return unknown, errors.New("invalid Home target geometry or counts")
		}
		if missing > 0 || row.Blocker != "" {
			row.Cells = append([]domain.Cell{}, row.Cells...)
			result = append(result, row)
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return domain.Known(result), nil
}

// StoneShellResearch is the native project whose recipes cut the stone
// blocks a wall replacement needs; a candidate with no replacement material
// while it is unfinished is waiting on research, not on a site.
const StoneShellResearch = "Stonecutting"

func ReviewStoneShell(owned domain.Fact[[]ConstructionClaim], structures domain.Fact[[]StoneStructure]) (domain.Fact[[]string], error) {
	unknown := domain.Unknown[[]string]()
	buildings, known := owned.Value()
	if !known {
		return unknown, nil
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
