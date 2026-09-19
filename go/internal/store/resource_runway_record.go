package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type ResourceRunwayRecord struct {
	Resource                               policy.Resource
	Tick                                   domain.Tick
	WindowDays                             float64
	Reserve                                int64
	Stock, SurfaceOre                      *int64
	ConsumptionPerDay, StockDays, DaysLeft *float64
	Deficit                                *bool
	Target                                 int64
}

func runwayPointer[T any](f domain.Fact[T]) *T {
	v, known := f.Value()
	if !known {
		return nil
	}
	return &v
}

func runwayFact[T any](v *T) domain.Fact[T] {
	if v == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*v)
}

func resourceRunwayRecords(rows []policy.ResourceRunway) []ResourceRunwayRecord {
	var out []ResourceRunwayRecord
	for _, r := range rows {
		out = append(out, ResourceRunwayRecord{Resource: r.Resource, Tick: r.Tick, WindowDays: r.WindowDays, Reserve: r.Reserve,
			Stock: runwayPointer(r.Stock), SurfaceOre: runwayPointer(r.SurfaceOre), ConsumptionPerDay: runwayPointer(r.ConsumptionPerDay), StockDays: runwayPointer(r.StockDays), DaysLeft: runwayPointer(r.DaysLeft), Deficit: runwayPointer(r.Deficit), Target: r.Target})
	}
	return out
}

func (r RoutineReview) ResourceRunwayState() []policy.ResourceRunway {
	var out []policy.ResourceRunway
	for _, row := range r.ResourceRunways {
		out = append(out, policy.ResourceRunway{Resource: row.Resource, Tick: row.Tick, WindowDays: row.WindowDays, Reserve: row.Reserve,
			Stock: runwayFact(row.Stock), SurfaceOre: runwayFact(row.SurfaceOre), ConsumptionPerDay: runwayFact(row.ConsumptionPerDay), StockDays: runwayFact(row.StockDays), DaysLeft: runwayFact(row.DaysLeft), Deficit: runwayFact(row.Deficit), Target: row.Target})
	}
	return out
}
