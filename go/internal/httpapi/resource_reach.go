package httpapi

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type roundsExtentDTO struct {
	Known   bool `json:"known"`
	Regions int  `json:"regions"`
	Cells   int  `json:"cells"`
}

type lootHoldDTO struct {
	Thing      string `json:"thing"`
	Definition string `json:"definition"`
	X          int32  `json:"x"`
	Z          int32  `json:"z"`
	Reason     string `json:"reason"`
}

func lootHolds(rows []policy.LootHold) []lootHoldDTO {
	out := make([]lootHoldDTO, 0, len(rows))
	for _, row := range rows {
		out = append(out, lootHoldDTO{Thing: row.Thing, Definition: row.Definition, X: row.Cell.X, Z: row.Cell.Z, Reason: row.Reason})
	}
	return out
}

func roundsExtent(f domain.Fact[policy.ColonyExtent]) roundsExtentDTO {
	e, known := f.Value()
	d := roundsExtentDTO{Known: known, Regions: len(e.Regions)}
	for _, region := range e.Regions {
		d.Cells += len(region.Cells)
	}
	return d
}
