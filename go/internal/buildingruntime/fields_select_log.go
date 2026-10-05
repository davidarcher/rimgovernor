package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// fieldsSelectDecision is the fields_select row of one site-type selection:
// target the winning crop, reason the winning site kind, attrs the winner's
// cells, buildings, need and urgency, and every candidate with its score,
// term breakdown and (for an unplantable one) the reason.
func fieldsSelectDecision(selection policy.SiteTypePlan) telemetry.Decision {
	candidates := make([]map[string]any, len(selection.Candidates))
	for i, c := range selection.Candidates {
		row := map[string]any{"kind": string(c.Kind), "crop": c.Crop.Name, "needed": c.Needed, "cells": c.Cells, "score": c.Score}
		if len(c.Terms) > 0 {
			terms := make(map[string]float64, len(c.Terms))
			for _, t := range c.Terms {
				terms[t.Name] = t.Value
			}
			row["terms"] = terms
		}
		if c.Reason != "" {
			row["reason"] = c.Reason
		}
		candidates[i] = row
	}
	cells := 0
	if len(selection.Candidates) > 0 {
		cells = selection.Candidates[0].Cells
	}
	return telemetry.Decision{Kind: "fields_select", Component: "clock-scheduler", Verdict: "selected", Reason: string(selection.Kind), Target: selection.Crop.Name,
		Attrs: map[string]any{"cells": cells, "buildings": len(selection.Buildings), "needed": selection.Needed, "urgent": selection.Urgent, "candidates": candidates}}
}
