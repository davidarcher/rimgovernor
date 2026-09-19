package farm

import "testing"

func TestBasinSowsRequiresABuiltBasinOnTheCrop(t *testing.T) {
	basins := []any{
		map[string]any{"id": "b1", "stage": "blueprint", "crop": nil},
		map[string]any{"id": "b2", "stage": "built", "crop": "Plant_Rice"},
	}
	if err := basinSows(basins, "Plant_Potato"); err == nil {
		t.Fatal("a built basin still on rice passed")
	}
	if err := basinSows(basins[:1], "Plant_Potato"); err == nil {
		t.Fatal("no built basin passed")
	}
	basins = append(basins, map[string]any{"id": "b3", "stage": "built", "crop": "Plant_Potato"})
	if err := basinSows(basins, "Plant_Potato"); err != nil {
		t.Fatal(err)
	}
}

func TestSecondFieldRequiresNativePlantingAndSeparation(t *testing.T) {
	cell := func(x, z int) any { return map[string]any{"x": x, "z": z} }
	prepared := map[string]any{"zoneId": 1, "zoneCells": []any{cell(4, 4)}}
	zone := map[string]any{"id": 2, "cells": []any{cell(6, 4)}, "planted": 1}
	native := map[string]any{"zones": []any{zone}}
	if err := separatedPlantedField(native, prepared); err != nil {
		t.Fatal(err)
	}
	zone["planted"] = 0
	if err := separatedPlantedField(native, prepared); err == nil {
		t.Fatal("unsown zone passed")
	}
	zone["planted"], zone["cells"] = 1, []any{cell(5, 4)}
	if err := separatedPlantedField(native, prepared); err == nil {
		t.Fatal("adjacent field passed")
	}
}
