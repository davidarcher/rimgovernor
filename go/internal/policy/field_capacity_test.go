package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func TestFieldCoverageMixedCropsAndReserve(t *testing.T) {
	// Python field_target: rice=ceil(5*(3*2.5+7))=73,
	// corn=ceil(5*(10*2.5+7)/2)=80. Rice covers one target, corn half.
	rice := FieldCrop{Edible: domain.Known(true), GrowingCells: domain.Known(int64(73)), Demand: domain.Known(5.0), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0)}
	corn := rice
	corn.GrowingCells = domain.Known(int64(40))
	corn.GrowDays = domain.Known(10.0)
	corn.HarvestNutrition = domain.Known(2.0)
	rows := domain.Known([]FieldCrop{rice, corn, {Edible: domain.Known(false)}})
	got, known := FieldCoverage(domain.Known(int64(3)), rows, 7).Value()
	if !known || got != 1.5 {
		t.Fatal(got, known)
	}
	more, known := FieldCoverage(domain.Known(int64(3)), rows, 14).Value()
	if !known || more >= got {
		t.Fatal(more, known)
	}
	for _, change := range []string{"edible", "cells", "demand", "growth", "yield", "overflow"} {
		t.Run(change, func(t *testing.T) {
			bad := rice
			switch change {
			case "edible":
				bad.Edible = domain.Unknown[bool]()
			case "cells":
				bad.GrowingCells = domain.Unknown[int64]()
			case "demand":
				bad.Demand = domain.Unknown[float64]()
			case "growth":
				bad.GrowDays = domain.Known(0.0)
			case "yield":
				bad.HarvestNutrition = domain.Known(math.NaN())
			case "overflow":
				bad.Demand = domain.Known(math.MaxFloat64)
			}
			if _, known := FieldCoverage(domain.Known(int64(3)), domain.Known([]FieldCrop{bad}), 7).Value(); known {
				t.Fatal("unknown capacity certified")
			}
		})
	}
	if n, known := FieldCoverage(domain.Unknown[int64](), domain.Known([]FieldCrop{}), 7).Value(); !known || n != 0 {
		t.Fatal(n, known)
	}
	if _, known := FieldCoverage(domain.Known(int64(3)), domain.Unknown[[]FieldCrop](), 7).Value(); known {
		t.Fatal("missing census")
	}
}
