package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyHomeCoverage(v *o.ColonyFactsSnapshot) domain.Fact[policy.HomeCoverageObservation] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "home_coverage") {
		return domain.Unknown[policy.HomeCoverageObservation]()
	}
	h := u.GetHomeCoverage().GetObserved()
	if h == nil {
		return domain.Unknown[policy.HomeCoverageObservation]()
	}
	r := policy.HomeCoverageObservation{Revision: h.GetRevision(), Targets: []policy.HomeCoverageTarget{}}
	for _, row := range h.Targets {
		t := policy.HomeCoverageTarget{ID: row.GetId(), Shape: optional(row.ShapeToken), Blocker: row.GetBlocker(), Cells: []domain.Cell{}}
		if row.MissingCells != nil {
			t.Missing = domain.Known(int64(row.GetMissingCells()))
		}
		if row.ExcludedCells != nil {
			t.Excluded = domain.Known(int64(row.GetExcludedCells()))
		}
		for _, c := range row.Cells {
			t.Cells = append(t.Cells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
		}
		if geometry := row.ExtentGeometry; geometry != nil {
			g := policy.HomeExtentGeometry{}
			for _, c := range geometry.EnclosedInterior {
				g.EnclosedInterior = append(g.EnclosedInterior, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			for _, c := range geometry.Corridor {
				g.Corridor = append(g.Corridor, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			t.ExtentGeometry = domain.Known(g)
		}
		r.Targets = append(r.Targets, t)
	}
	return domain.Known(r)
}
func colonyStoneStructures(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.StoneStructure] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "structures") {
		return domain.Unknown[[]policy.StoneStructure]()
	}
	rows := []policy.StoneStructure{}
	for _, r := range u.Structures {
		rows = append(rows, policy.StoneStructure{ID: r.Building.Building.GetId(), Definition: policy.Resource(r.Building.Building.GetDefName()), Flammability: optional(r.Flammability)})
	}
	return domain.Known(rows)
}
