package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStatusRowsSeverityAndOrder(t *testing.T) {
	rows := StatusRows(StatusInput{
		Progress:  []GoalProgress{{Goal: "MaintainFood", Method: "hunt", Blocked: "no_target"}, {Goal: "EnsureShelter", Method: "build"}},
		Colonists: domain.Known(int64(3)),
		FoodDays:  domain.Known(1.5),
		Wood:      domain.Known(int64(40)),
		WoodFloor: 100,
		Emergency: []GoalID{"ManageSupplySafety"},
		Refusals:  []RefusalMarker{{Label: "Building no_path", Cell: domain.Cell{X: 4, Z: 5}, Tick: 10}},
	})
	want := []struct {
		key      string
		text     string
		severity StatusSeverity
		detail   bool
	}{
		{"goal", "goal EnsureShelter: build (3 pawns)", StatusInfo, false},
		{"food", "food 1.5 days", StatusCritical, false},
		{"wood", "wood 40/100", StatusWarning, false},
		{"emergency", "EMERGENCY ManageSupplySafety", StatusCritical, false},
		{"refusal", "refused Building no_path", StatusWarning, false},
		{"goal.MaintainFood", "MaintainFood: hunt - no_target", StatusInfo, true},
		{"goal.EnsureShelter", "EnsureShelter: build", StatusInfo, true},
	}
	if len(rows) != len(want) {
		t.Fatalf("%+v", rows)
	}
	for i, w := range want {
		r := rows[i]
		if r.Key != w.key || r.Text != w.text || r.Severity != w.severity || r.Detail != w.detail {
			t.Fatalf("row %d = %+v, want %+v", i, r, w)
		}
	}
	if cell, ok := rows[4].Target.Value(); !ok || cell != (domain.Cell{X: 4, Z: 5}) {
		t.Fatalf("refusal target %+v", rows[4].Target)
	}
	if rows := StatusRows(StatusInput{FoodDays: domain.Known(4.0)}); rows[0].Severity != StatusWarning {
		t.Fatalf("%+v", rows)
	}
}

func TestRefusalOverlayExpiresAfterADay(t *testing.T) {
	overlay := RefusalOverlay([]RefusalMarker{
		{Label: "old", Cell: domain.Cell{X: 1, Z: 1}, Tick: 0},
		{Label: "older", Cell: domain.Cell{X: 2, Z: 2}, Tick: 50000},
		{Label: "new", Cell: domain.Cell{X: 3, Z: 3}, Tick: 70000},
	}, 70000)
	if len(overlay.Layers) != 2 || overlay.Labels[0].Text != "new" || overlay.Labels[1].Text != "older" {
		t.Fatalf("%+v", overlay)
	}
	l := overlay.Layers[0]
	if l.Style != OverlayOutline || l.Color.R < 0.5 || l.Rects[0] != (Rectangle{X: 3, Z: 3, Width: 1, Height: 1}) {
		t.Fatalf("%+v", l)
	}
}
