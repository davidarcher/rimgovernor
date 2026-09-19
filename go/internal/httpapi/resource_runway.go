package httpapi

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type resourceRunwayDTO struct {
	Resource          policy.Resource `json:"resource"`
	Tick              domain.Tick     `json:"tick"`
	WindowDays        float64         `json:"windowDays"`
	ThresholdDays     float64         `json:"thresholdDays"`
	Reserve           int64           `json:"reserve"`
	Stock             *int64          `json:"stock"`
	SurfaceOre        *int64          `json:"surfaceOre"`
	ConsumptionPerDay *float64        `json:"consumptionPerDay"`
	StockDays         *float64        `json:"stockDays"`
	DaysLeft          *float64        `json:"daysLeft"`
	Deficit           *bool           `json:"deficit"`
	Target            int64           `json:"target"`
}

func resourceRunwaysDTO(rows []policy.ResourceRunway) []resourceRunwayDTO {
	out := []resourceRunwayDTO{}
	for _, r := range rows {
		out = append(out, resourceRunwayDTO{Resource: r.Resource, Tick: r.Tick, WindowDays: r.WindowDays, ThresholdDays: policy.ResourceRunwayDays, Reserve: r.Reserve,
			Stock: factPointer(r.Stock), SurfaceOre: factPointer(r.SurfaceOre), ConsumptionPerDay: factPointer(r.ConsumptionPerDay), StockDays: factPointer(r.StockDays), DaysLeft: factPointer(r.DaysLeft), Deficit: factPointer(r.Deficit), Target: r.Target})
	}
	return out
}
