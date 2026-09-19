package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func facilityClaim(t *testing.T, id, def, stuff string) ConstructionClaim {
	t.Helper()
	b, err := domain.NewBuilding(def, domain.Cell{X: 3, Z: 7}, domain.North, stuff)
	if err != nil {
		t.Fatal(err)
	}
	return ConstructionClaim{Plan: "method", Action: domain.ActionID(id), Goal: "goal", Identity: domain.ConstructionIdentity{Origin: "blueprint-" + id, Current: id}, Building: b}
}

func TestHomeCoverageRestoresMissingCellsAndRequiresOwnedTargets(t *testing.T) {
	claim := facilityClaim(t, "wall", "Wall", "WoodLog")
	row := HomeCoverageTarget{ID: "wall", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Excluded: domain.Known(int64(1)), Cells: []domain.Cell{{X: 3, Z: 7}}}
	census := HomeCoverageObservation{Revision: 4, Targets: []HomeCoverageTarget{row, {ID: "player-wall"}}}
	got, err := ReviewHomeCoverage(domain.Known([]ConstructionClaim{claim}), domain.Known([]OwnedStockpile{}), domain.Known(census))
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].ID != "wall" || rows[0].Excluded != domain.Known(int64(1)) {
		t.Fatal(rows, known, err)
	}
	rows[0].Cells[0].X = 99
	if census.Targets[0].Cells[0].X != 3 {
		t.Fatal("target aliases native input")
	}
	// Native Home coverage omits fully covered targets; absence is recovery only
	// after exact current-building ownership and a complete Home read are known.
	got, err = ReviewHomeCoverage(domain.Known([]ConstructionClaim{claim}), domain.Known([]OwnedStockpile{}), domain.Known(HomeCoverageObservation{Revision: 5}))
	rows, known = got.Value()
	if err != nil || !known || len(rows) != 0 {
		t.Fatal(rows, known, err)
	}
}

func TestHomeCoverageUnknownsAndMalformedGeometry(t *testing.T) {
	claim := facilityClaim(t, "wall", "Wall", "WoodLog")
	for _, kind := range []string{"ownership", "zones", "census", "shape", "missing", "excluded", "cells", "bad-count", "duplicate"} {
		t.Run(kind, func(t *testing.T) {
			owned, zones := domain.Known([]ConstructionClaim{claim}), domain.Known([]OwnedStockpile{})
			row := HomeCoverageTarget{ID: "wall", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Excluded: domain.Known(int64(0)), Cells: []domain.Cell{{X: 3, Z: 7}}}
			switch kind {
			case "ownership":
				owned = domain.Unknown[[]ConstructionClaim]()
			case "zones":
				zones = domain.Unknown[[]OwnedStockpile]()
			case "shape":
				row.Shape = domain.Unknown[string]()
			case "missing":
				row.Missing = domain.Unknown[int64]()
			case "excluded":
				row.Excluded = domain.Unknown[int64]()
			case "cells":
				row.Cells = nil
			case "bad-count":
				row.Excluded = domain.Known(int64(2))
			}
			census := HomeCoverageObservation{Targets: []HomeCoverageTarget{row}}
			if kind == "duplicate" {
				census.Targets = append(census.Targets, row)
			}
			observed := domain.Known(census)
			if kind == "census" {
				observed = domain.Unknown[HomeCoverageObservation]()
			}
			got, err := ReviewHomeCoverage(owned, zones, observed)
			_, known := got.Value()
			if known || (err != nil) != (kind == "bad-count" || kind == "duplicate") {
				t.Fatal(got, err)
			}
		})
	}
}

func TestHomeCoverageBlocksChangedOwnedStockpileFootprint(t *testing.T) {
	zone := OwnedStockpile{ID: "zone", Cells: []domain.Cell{{X: 3, Z: 7}}}
	row := HomeCoverageTarget{ID: "zone", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Excluded: domain.Known(int64(0)), Cells: []domain.Cell{{X: 3, Z: 8}}}
	got, err := ReviewHomeCoverage(domain.Known([]ConstructionClaim{}), domain.Known([]OwnedStockpile{zone}), domain.Known(HomeCoverageObservation{Targets: []HomeCoverageTarget{row}}))
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Blocker == "" {
		t.Fatal(rows, known, err)
	}
}

func TestSelectHomeCoverageMethodSkipsCoveredBlockedAndSeenThenPicksFirst(t *testing.T) {
	rows := []HomeCoverageTarget{
		{ID: "a", Shape: domain.Known("shape"), Missing: domain.Known(int64(0))},
		{ID: "b", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Blocker: "geometry unavailable"},
		{ID: "c", Shape: domain.Known("shape"), Missing: domain.Known(int64(1))},
		{ID: "d", Shape: domain.Known("shape"), Missing: domain.Known(int64(1))},
	}
	seenID := homeCoverageMethodID("c", "shape", 4)
	choice, err := SelectHomeCoverageMethod(domain.Known(rows), 4, []domain.MethodID{seenID})
	if err != nil || choice.Kind != HomeCoverageExtend || choice.Target != "d" || choice.Shape != "shape" || choice.Revision != 4 {
		t.Fatal(choice, err)
	}
	if choice.ID != homeCoverageMethodID("d", "shape", 4) {
		t.Fatal("method id not stable", choice.ID)
	}
}

func TestSelectHomeCoverageMethodRecoveredUnknownAndBlocked(t *testing.T) {
	if choice, err := SelectHomeCoverageMethod(domain.Unknown[[]HomeCoverageTarget](), 0, nil); err != nil || choice.Kind != HomeCoverageUnknown {
		t.Fatal(choice, err)
	}
	if choice, err := SelectHomeCoverageMethod(domain.Known([]HomeCoverageTarget{}), 0, nil); err != nil || choice.Kind != HomeCoverageRecovered {
		t.Fatal(choice, err)
	}
	blocked := []HomeCoverageTarget{{ID: "a", Blocker: "player edit"}}
	if choice, err := SelectHomeCoverageMethod(domain.Known(blocked), 0, nil); err != nil || choice.Kind != HomeCoverageBlocked {
		t.Fatal(choice, err)
	}
	all := []HomeCoverageTarget{{ID: "a", Shape: domain.Known("s"), Missing: domain.Known(int64(1))}}
	seen := []domain.MethodID{homeCoverageMethodID("a", "s", 0)}
	if choice, err := SelectHomeCoverageMethod(domain.Known(all), 0, seen); err != nil || choice.Kind != HomeCoverageBlocked {
		t.Fatal(choice, err)
	}
}

func TestStoneShellOnlyTargetsCurrentOwnedFlammableWalls(t *testing.T) {
	wood, stone, spot := facilityClaim(t, "wood", "Wall", "WoodLog"), facilityClaim(t, "stone", "Wall", "BlocksGranite"), facilityClaim(t, "spot", "SleepingSpot", "")
	rows := []StoneStructure{{ID: "wood", Definition: "Wall", Flammability: domain.Known(1.0)}, {ID: "stone", Definition: "Wall", Flammability: domain.Known(0.0)}, {ID: "player-wall", Definition: "Wall", Flammability: domain.Known(1.0)}}
	got, err := ReviewStoneShell(domain.Known([]ConstructionClaim{wood, stone, spot}), domain.Known(rows))
	targets, known := got.Value()
	if err != nil || !known || len(targets) != 1 || targets[0] != "wood" {
		t.Fatal(targets, known, err)
	}
	for _, kind := range []string{"missing", "unknown", "definition", "nan", "negative"} {
		t.Run(kind, func(t *testing.T) {
			r := rows[0]
			switch kind {
			case "unknown":
				r.Flammability = domain.Unknown[float64]()
			case "definition":
				r.Definition = "Door"
			case "nan":
				r.Flammability = domain.Known(math.NaN())
			case "negative":
				r.Flammability = domain.Known(-1.0)
			}
			census := []StoneStructure{r}
			if kind == "missing" {
				census = nil
			}
			got, err := ReviewStoneShell(domain.Known([]ConstructionClaim{wood}), domain.Known(census))
			_, known := got.Value()
			if known || (err != nil) != (kind == "nan" || kind == "negative") {
				t.Fatal(got, err)
			}
		})
	}
}

func TestConstructionOwnershipRequiresQueryCoverageForEveryCurrentClaim(t *testing.T) {
	claim := facilityClaim(t, "wall", "Wall", "WoodLog")
	got, err := OwnedConstructions(domain.Known([]ConstructionClaim{claim}), domain.Known(CurrentConstruction{Requested: []string{"older-wall"}}))
	if _, known := got.Value(); err != nil || known {
		t.Fatal("unqueried construction treated as absent", got, err)
	}
}
