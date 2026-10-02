package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func validStatusKey(k string) bool {
	if len(k) == 0 || len(k) > MaxStatusKey {
		return false
	}
	for i := 0; i < len(k); i++ {
		if k[i] < 0x21 || k[i] > 0x7e {
			return false
		}
	}
	return true
}

// Every key the bridge sends must pass the native 1-32 printable rule (#1263).
func TestStatusKeyAlwaysNativeValid(t *testing.T) {
	long := "goal.clearance_shrine_breach_repair_family_1234"
	id := GoalID("clearance.shrine-breach.repair.wall.12.34")
	rows := StatusRows(StatusInput{Progress: []GoalProgress{{Goal: id}}})
	keys := []string{"", "food", "goal held", "incident.é", long, long + "x"}
	for _, r := range rows {
		keys = append(keys, r.Key)
	}
	for _, k := range keys {
		if got := StatusKey(k); !validStatusKey(got) {
			t.Fatalf("StatusKey(%q) = %q, not 1-32 printable ASCII", k, got)
		}
	}
	if StatusKey("food") != "food" {
		t.Fatal("short valid key changed")
	}
	if StatusKey(long) == StatusKey(long+"x") {
		t.Fatal("distinct long keys collided")
	}
}

func TestStatusRowsSeverityAndOrder(t *testing.T) {
	rows := StatusRows(StatusInput{
		Progress:  []GoalProgress{{Goal: "EnsureFoodSupply", Method: "hunt", Blocked: "no_target"}, {Goal: "EnsureBasicPower", Method: "build"}},
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
		{"goal", "goal EnsureBasicPower: build (3 pawns)", StatusInfo, false},
		{"food", "food 1.5 days", StatusCritical, false},
		{"wood", "wood 40/100", StatusWarning, false},
		{"population", "population ?/100, intent ?, downed raiders die ?, unrecruitable ?", StatusInfo, false},
		{"emergency", "EMERGENCY ManageSupplySafety", StatusCritical, false},
		{"refusal", "refused Building no_path", StatusWarning, false},
		{"domain.Food", "Food", StatusInfo, true},
		{"goal.EnsureFoodSupply", "Standard EnsureFoodSupply: hunt - no target", StatusWarning, true},
		{"domain.Industry", "Industry", StatusInfo, true},
		{"goal.EnsureBasicPower", "Project EnsureBasicPower: build", StatusInfo, true},
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
	if cell, ok := rows[5].Target.Value(); !ok || cell != (domain.Cell{X: 4, Z: 5}) {
		t.Fatalf("refusal target %+v", rows[5].Target)
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

func TestStatusRowsPopulationOutlook(t *testing.T) {
	r, ok := statusRow(StatusRows(StatusInput{Outlook: PopulationOutlook{
		Intent:              domain.Known(0.35),
		AdjustedPopulation:  domain.Known(9.5),
		DeathOnDownedChance: domain.Known(0.62),
		UnrecruitableChance: domain.Known(0.12),
	}}), "population")
	if !ok || r.Text != "population 9.5/100, intent 0.35, downed raiders die 62%, unrecruitable 12%" || r.Detail {
		t.Fatalf("%+v", r)
	}
	r, ok = statusRow(StatusRows(StatusInput{Outlook: PopulationOutlook{Intent: domain.Known(-0.2)}}), "population")
	if !ok || r.Text != "population ?/100, intent -0.20, downed raiders die ?, unrecruitable ?" {
		t.Fatalf("%+v", r)
	}
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

// Detail rows sit under their domain's heading with their concept; open
// incidents get incident.<id> rows; System and closed incidents get none.
func TestStatusRowsGroupDetailByDomain(t *testing.T) {
	rows := StatusRows(StatusInput{
		Progress: []GoalProgress{
			{Goal: MaintainWaste, Method: "haul"},
			{Goal: EnsureFoodSupply, Method: "hunt"},
			{Goal: MaintainRefrigeration, Blocked: HeldOptIn},
		},
		Incidents: []domain.Incident{
			{ID: "inc-1", Kind: ActiveCombat},
			{ID: "inc-2", Kind: CriticalMedicine, Subject: "pawn7"},
			{ID: "inc-3", Kind: AnswerDialog},
			{ID: "inc-4", Kind: ActiveCombat, Closed: true},
		},
	})
	var got []string
	for _, r := range rows {
		if r.Detail {
			got = append(got, r.Key+"|"+r.Text)
		}
	}
	want := []string{
		"domain.Food|Food",
		"goal.EnsureFoodSupply|Standard EnsureFoodSupply: hunt",
		"goal.held.Food|held MaintainRefrigeration",
		"domain.Military|Military",
		"incident.inc-1|Incident ActiveCombat",
		"domain.Medical|Medical",
		"incident.inc-2|Incident CriticalMedical pawn7",
		"domain.Upkeep|Upkeep",
		"goal.MaintainWaste|Standard MaintainWaste: haul",
	}
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("detail rows\n got %q\nwant %q", got, want)
	}
}
