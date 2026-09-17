package main

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
