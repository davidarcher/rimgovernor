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
		{"goal.MaintainFood", "MaintainFood: hunt - no_target", StatusWarning, true},
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

func statusRow(rows []StatusRow, key string) (StatusRow, bool) {
	for _, r := range rows {
		if r.Key == key {
			return r, true
		}
	}
	return StatusRow{}, false
}

func TestStatusRowsPause(t *testing.T) {
	if _, ok := statusRow(StatusRows(StatusInput{}), "pause"); ok {
		t.Fatal("a running clock drew a pause row")
	}
	for _, c := range []struct {
		pause    ClockPause
		text     string
		severity StatusSeverity
	}{
		{ClockPause{By: "player", Reason: "external_pause"}, "paused by player: external_pause", StatusInfo},
		{ClockPause{By: "letter", Reason: "letter_pause", Held: true}, "paused by letter: letter_pause", StatusWarning},
		{ClockPause{By: "hold", Reason: "hostile", Held: true}, "paused by hold: hostile", StatusWarning},
	} {
		r, ok := statusRow(StatusRows(StatusInput{Pause: c.pause}), "pause")
		if !ok || r.Text != c.text || r.Severity != c.severity {
			t.Fatalf("%+v: %+v", c.pause, r)
		}
	}
}

func TestStatusRowsMedicineThresholds(t *testing.T) {
	if _, ok := statusRow(StatusRows(StatusInput{MedicineTarget: 9}), "medicine"); ok {
		t.Fatal("unknown stock drew a medicine row")
	}
	if _, ok := statusRow(StatusRows(StatusInput{Medicine: domain.Known(int64(4))}), "medicine"); ok {
		t.Fatal("no target drew a medicine row")
	}
	for _, c := range []struct {
		stock    int64
		severity StatusSeverity
	}{{8, StatusWarning}, {9, StatusInfo}, {12, StatusInfo}} {
		r, ok := statusRow(StatusRows(StatusInput{Medicine: domain.Known(c.stock), MedicineTarget: 9}), "medicine")
		if !ok || r.Severity != c.severity {
			t.Fatalf("stock %d: %+v", c.stock, r)
		}
	}
}

func TestStatusRowsGoalTargetCells(t *testing.T) {
	cell := domain.Cell{X: 7, Z: 9}
	rows := StatusRows(StatusInput{
		Progress:  []GoalProgress{{Goal: "EnsureShelter", Method: "build"}, {Goal: "MaintainFood", Method: "hunt"}},
		GoalCells: map[GoalID]domain.Cell{"EnsureShelter": cell},
	})
	for _, key := range []string{"goal", "goal.EnsureShelter"} {
		r, _ := statusRow(rows, key)
		if got, known := r.Target.Value(); !known || got != cell {
			t.Fatalf("%s target = %+v", key, r.Target)
		}
	}
	if r, _ := statusRow(rows, "goal.MaintainFood"); r.Target != (domain.Fact[domain.Cell]{}) {
		t.Fatalf("goal without a cell targeted %+v", r.Target)
	}
}
