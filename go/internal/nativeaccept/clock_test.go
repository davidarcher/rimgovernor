package nativeaccept

import (
	"database/sql"
	"encoding/json"
	"path/filepath"
	"strconv"
	"testing"
)

func clockTrace(names ...string) ([]map[string]any, map[string][]string) {
	caps := map[string][]string{}
	rows := make([]map[string]any, len(names))
	for i, name := range names {
		caps[name] = []string{"rimgovernor/" + name}
		rows[i] = map[string]any{"Sequence": i + 1, "OperationId": strconv.Itoa(i), "CapabilityId": name}
	}
	return rows, caps
}

func TestDisabledRestartCannotHideWrites(t *testing.T) {
	for _, name := range []string{"clock_start", "clock_renew", "operations_execute", "authority_control"} {
		t.Run(name, func(t *testing.T) {
			rows, caps := clockTrace("clock_read_events", name)
			if _, err := ClockAudit(rows, 0, caps, true, false); err == nil {
				t.Fatalf("expected an error for a disabled restart exposing %s", name)
			}
		})
	}
}

func TestGoClockTraceRequiresContiguousAttributedControl(t *testing.T) {
	rows, caps := clockTrace("clock_read_events", "clock_start", "operations_execute", "clock_pause")
	names, err := ClockAudit(rows, 0, caps, false, false)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(names) != 4 {
		t.Fatalf("expected 4 operations, got %d", len(names))
	}
	if _, err := ClockAudit(rows[1:], 0, caps, false, false); err == nil {
		t.Fatal("expected an error for a truncated trace")
	}
	if _, err := ClockAudit(append(append([]map[string]any{}, rows...), rows[len(rows)-1]), 0, caps, false, false); err == nil {
		t.Fatal("expected an error for a duplicated-sequence trace")
	}
	caps["clock_start"] = []string{"home/status"}
	if _, err := ClockAudit(rows, 0, caps, false, false); err == nil {
		t.Fatal("expected an error once clock_start no longer maps to an allowed capability")
	}
}

func TestInterruptionRequiresExactNativeLetterStop(t *testing.T) {
	event := map[string]any{"stopped": map[string]any{"reason": "STOP_REASON_LETTER_PAUSE", "pause": map[string]any{"letter": map[string]any{"id": "Letter_1"}}}}
	got, err := InterruptedByLetter([]map[string]any{event}, "Letter_1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !DeepEqual(got, event) {
		t.Fatalf("unexpected event returned: %#v", got)
	}
	changed := deepCopyGearAny(event).(map[string]any)
	changedStopped, _ := AsMap(changed["stopped"])
	changedStopped["reason"] = "STOP_REASON_REQUESTED_PAUSE"
	for _, values := range [][]map[string]any{{}, {changed}, {event, event}} {
		if _, err := InterruptedByLetter(values, "Letter_1"); err == nil {
			t.Fatalf("expected an error for %#v", values)
		}
	}
	if _, err := InterruptedByLetter([]map[string]any{event}, "Letter_2"); err == nil {
		t.Fatal("expected an error for a mismatched letter id")
	}
}

func TestRoutineTraceRequiresReadAndRejectsItOnDisabledRestart(t *testing.T) {
	rows, caps := clockTrace("clock_read_events", "clock_start", "operations_execute", "clock_pause")
	if _, err := ClockAudit(rows, 0, caps, false, true); err == nil {
		t.Fatal("expected an error requiring a routine-reviews read that never happened")
	}
	rows, caps = clockTrace("clock_read_events", "clock_start", "operations_execute", "clock_pause", "observations_read_colony_facts")
	names, err := ClockAudit(rows, 0, caps, false, true)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(names) != 5 {
		t.Fatalf("expected 5 operations, got %d", len(names))
	}
	rows, caps = clockTrace("clock_read_events", "observations_read_colony_facts")
	if _, err := ClockAudit(rows, 0, caps, true, true); err == nil {
		t.Fatal("expected an error for a restarted trace still claiming a routine-reviews read")
	}
}

func clockHealthyPawnFixture(field string) map[string]any {
	pawn := map[string]any{
		"pawn": map[string]any{"id": "pawn"}, "dead": false, "downed": false,
		"health": map[string]any{"bleeding": false, "needsTend": false},
	}
	switch field {
	case "dead", "downed":
		pawn[field] = true
	case "bleeding", "needsTend":
		health, _ := AsMap(pawn["health"])
		health[field] = true
	case "unknown":
		health, _ := AsMap(pawn["health"])
		delete(health, "needsTend")
	}
	// The reply as the game returns it for filter colonist=true: pawns and
	// completeness directly under observed, filtered counting non-colonists.
	return map[string]any{"observed": map[string]any{
		"pawns": []any{pawn},
		"completeness": map[string]any{
			"page": map[string]any{"complete": true}, "matched": "1", "returned": "1", "filtered": "1", "unreadable": "0",
		},
	}}
}

func TestClockFixtureReportsMedicalPrerequisiteBeforeRunning(t *testing.T) {
	for _, field := range []string{"", "dead", "downed", "bleeding", "needsTend", "unknown", "unreadable", "incomplete"} {
		t.Run(field, func(t *testing.T) {
			reply := clockHealthyPawnFixture(field)
			observed, _ := AsMap(reply["observed"])
			completeness, _ := AsMap(observed["completeness"])
			switch field {
			case "unreadable":
				completeness["unreadable"] = "1"
			case "incomplete":
				completeness["page"] = map[string]any{"complete": false}
			}
			err := RequireHealthyColonists(reply)
			if field == "" {
				if err != nil {
					t.Fatalf("unexpected error: %v", err)
				}
				return
			}
			if err == nil {
				t.Fatalf("expected a prerequisite error for field %q", field)
			}
		})
	}
}

func writeRoutineReviewDB(t *testing.T, path string, fault string) {
	t.Helper()
	needs := routineNeeds
	scope := map[string]any{"Colony": "colony", "Load": "load", "Map": 0}
	goalsList := make([]map[string]any, len(needs))
	for i, name := range needs {
		goalsList[i] = map[string]any{"Need": name, "Goal": name}
	}
	review := map[string]any{"Revision": 2, "Enabled": false, "Snapshot": scope, "Tick": 7, "Goals": goalsList}
	reviewJSON, err := json.Marshal(review)
	if err != nil {
		t.Fatalf("marshal review: %v", err)
	}

	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(path))
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	defer db.Close()
	statements := []string{
		"CREATE TABLE routine_review(singleton,payload)",
		"CREATE TABLE goals(id,payload)",
		"CREATE TABLE goal_methods(id)",
	}
	for _, stmt := range statements {
		if _, err := db.Exec(stmt); err != nil {
			t.Fatalf("exec %q: %v", stmt, err)
		}
	}
	if _, err := db.Exec("INSERT INTO routine_review VALUES(1,?)", string(reviewJSON)); err != nil {
		t.Fatalf("insert review: %v", err)
	}
	for _, name := range needs {
		goal := map[string]any{"Source": "autopilot", "Snapshot": copyCompatAny(scope), "Tick": 7, "Status": "invalidated", "Need": "unknown"}
		if name == "EnsureFoodSupply" {
			switch fault {
			case "food":
				goal["Need"] = "recovered"
			case "world":
				snapshot, _ := AsMap(goal["Snapshot"])
				snapshot["Load"] = "other"
			case "active":
				goal["Status"] = "active"
			case "missing":
				continue
			}
		}
		goalJSON, err := json.Marshal(goal)
		if err != nil {
			t.Fatalf("marshal goal: %v", err)
		}
		if _, err := db.Exec("INSERT INTO goals VALUES(?,?)", name, string(goalJSON)); err != nil {
			t.Fatalf("insert goal: %v", err)
		}
	}
	if fault == "method" {
		if _, err := db.Exec("INSERT INTO goal_methods VALUES(1)"); err != nil {
			t.Fatalf("insert goal_methods: %v", err)
		}
	}
}

func TestRoutineEvidenceRequiresNativeScopeUnknownForecastAndManualRetirement(t *testing.T) {
	for _, fault := range []string{"", "food", "world", "missing", "active", "method"} {
		t.Run(fault, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "review.sqlite")
			writeRoutineReviewDB(t, path, fault)
			identity := map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 0}
			expected := "unknown"
			result, err := RoutineEvidence(path, identity, false, &expected, false)
			if fault != "" {
				if err == nil {
					t.Fatalf("expected an error for fault %q", fault)
				}
				return
			}
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			goals, _ := result["goals"].(map[string]map[string]any)
			if len(goals) != len(routineNeeds) {
				t.Fatalf("expected %d goals, got %d", len(routineNeeds), len(goals))
			}
		})
	}
}
