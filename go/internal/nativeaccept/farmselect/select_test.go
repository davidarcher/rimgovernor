package farmselect

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func selectRow(kind, crop string, cells int, urgent bool, candidates ...any) na.FlightRow {
	return na.FlightRow{Kind: "fields_select", Payload: map[string]any{
		"verdict": "selected", "reason": kind, "target": crop, "dur_ms": 0.0,
		"attrs": map[string]any{"cells": float64(cells), "buildings": 0.0, "needed": 671.0, "urgent": urgent, "candidates": candidates},
	}}
}

func candidate(kind, crop string, needed, cells int, score float64, terms map[string]any, reason string) map[string]any {
	c := map[string]any{"kind": kind, "crop": crop, "needed": float64(needed), "cells": float64(cells), "score": score}
	if terms != nil {
		c["terms"] = terms
	}
	if reason != "" {
		c["reason"] = reason
	}
	return c
}

func rows(winnerTerms map[string]any, loserReason string) []na.FlightRow {
	return []na.FlightRow{
		{Kind: "planner_step", Payload: map[string]any{}},
		selectRow("outdoor", "Plant_Rice", 224, true,
			candidate("outdoor", "Plant_Rice", 671, 224, 0.021, winnerTerms, "net 14.0900/day over 15 patches"),
			candidate("outdoor", "Plant_Potato", 0, 0, 0, nil, loserReason),
			candidate("greenhouse-new", "Plant_Rice", 671, 0, 0, nil, "sun lamp unavailable")),
		selectRow("greenhouse-reuse", "Plant_Corn", 59, false,
			candidate("greenhouse-reuse", "Plant_Corn", 55, 59, 0.0411, map[string]any{"yield": 5.9, "travel": -2.3385}, "")),
	}
}

var riceTerms = map[string]any{"yield": 22.4, "travel": -3.9, "hauling": -1.3, "perimeter": -2.56, "fragment": -0.75}

func TestParseAndCheck(t *testing.T) {
	selections, err := Parse(rows(riceTerms, "season too short"))
	if err != nil || len(selections) != 2 {
		t.Fatal(selections, err)
	}
	first := selections[0]
	if first.Kind != "outdoor" || first.Crop != "Plant_Rice" || first.Cells != 224 || !first.Urgent || len(first.Candidates) != 3 {
		t.Fatalf("%+v", first)
	}
	if c := first.Candidates[0]; c.Terms["yield"] != 22.4 || c.Terms["fragment"] != -0.75 || c.Reason != "net 14.0900/day over 15 patches" {
		t.Fatalf("%+v", c)
	}
	if c := first.Candidates[2]; c.Kind != "greenhouse-new" || c.Reason != "sun lamp unavailable" || c.Terms != nil {
		t.Fatalf("%+v", c)
	}
	if _, err := Check(selections, Expectation{Kind: "outdoor"}); err == nil {
		t.Fatal("second selection's kind was accepted")
	}
	last, err := Check(selections[:1], Expectation{Kind: "outdoor", Crop: "Plant_Rice", MinCells: 100})
	if err != nil || last.Cells != 224 {
		t.Fatal(last, err)
	}
	if _, err := Check(nil, Expectation{}); err == nil {
		t.Fatal("empty recording passed")
	}
	// A winner without terms, or a loser without a reason, fails.
	selections, _ = Parse(rows(nil, "season too short"))
	if _, err := Check(selections[:1], Expectation{}); err == nil {
		t.Fatal("winner without breakdown passed")
	}
	selections, _ = Parse(rows(riceTerms, ""))
	if _, err := Check(selections[:1], Expectation{}); err == nil {
		t.Fatal("loser without reason passed")
	}
}

func TestResilienceTermsAreParsedAndRequired(t *testing.T) {
	terms := map[string]any{"labor": -0.2, "harvest-delay": -0.3, "risk-frost": 2.0, "yield": 5.9}
	selections, err := Parse([]na.FlightRow{selectRow("greenhouse-reuse", "Plant_Corn", 59, false,
		candidate("greenhouse-reuse", "Plant_Corn", 55, 59, 0.0411, terms, ""))})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := Check(selections, Expectation{Terms: []string{"labor", "harvest-delay", "risk-frost"}}); err != nil {
		t.Fatal(err)
	}
	if _, err := Check(selections, Expectation{Terms: []string{"risk-fallout"}}); err == nil {
		t.Fatal("missing risk term passed")
	}
}
