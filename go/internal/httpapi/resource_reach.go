package httpapi

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type routineExtentDTO struct {
	Known   bool `json:"known"`
	Regions int  `json:"regions"`
	Cells   int  `json:"cells"`
}

func routineExtent(f domain.Fact[policy.ColonyExtent]) routineExtentDTO {
	e, known := f.Value()
	d := routineExtentDTO{Known: known, Regions: len(e.Regions)}
	for _, region := range e.Regions {
		d.Cells += len(region.Cells)
	}
	return d
}
