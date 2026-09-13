package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

func deepCopyRoutineAny(v any) any {
	return deepCopyGearAny(v)
}

func TestRoutineWaitReportsInterruptionWithoutAcknowledgingOrReacquiring(t *testing.T) {
	var calls [][2]string
	http := func(method, path string) (map[string]any, error) {
		calls = append(calls, [2]string{method, path})
		if path == "/api/state" {
			return map[string]any{"mode": "manual"}, nil
		}
		return map[string]any{"holds": []any{map[string]any{"kind": "interruption"}}}, nil
	}
	if err := AssertRoutineRunning(http); err == nil {
		t.Fatal("expected an interruption error")
	}
	want := [][2]string{{"GET", "/api/state"}, {"GET", "/api/player/clock"}}
	if len(calls) != len(want) || calls[0] != want[0] || calls[1] != want[1] {
		t.Fatalf("unexpected calls: %v", calls)
	}
	running := func(method, path string) (map[string]any, error) {
		if method != "GET" || path != "/api/state" {
			t.Fatalf("unexpected call: %s %s", method, path)
		}
		return map[string]any{"mode": "automate"}, nil
	}
	if err := AssertRoutineRunning(running); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestMedicalCareNativeEvidenceUsesReferenceAndRejectsFalseRecovery(t *testing.T) {
	expected := map[string]any{"patients": []any{"patient"}, "unknown": []any{}, "need": "deficit"}
	active := map[string]any{
		"review": map[string]any{"MedicalCare": map[string]any{"CensusKnown": true, "Patients": []any{"patient"}, "Unknown": nil}},
		"goals":  map[string]any{"MaintainMedicalCare": map[string]any{"Need": "deficit", "Priority": 2}},
	}
	if err := AuditMedicalCare(active, expected); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, mutation := range []struct {
		field string
		value any
	}{{"CensusKnown", false}, {"Patients", []any{}}, {"Unknown", []any{"missing"}}} {
		changed := deepCopyRoutineAny(active).(map[string]any)
		review, _ := AsMap(changed["review"])
		history, _ := AsMap(review["MedicalCare"])
		history[mutation.field] = mutation.value
		if err := AuditMedicalCare(changed, expected); err == nil {
			t.Fatalf("expected an error for field %s", mutation.field)
		}
	}
	goals, _ := AsMap(active["goals"])
	goal, _ := AsMap(goals["MaintainMedicalCare"])
	goal["Need"] = "recovered"
	if err := AuditMedicalCare(active, expected); err == nil {
		t.Fatal("expected an error once the goal need no longer matches")
	}
}

func TestStartingSupplyEvidenceRequiresExactOriginalCells(t *testing.T) {
	active := map[string]any{
		"review": map[string]any{"StartingSupplies": map[string]any{"Initialized": true, "Pending": []any{
			map[string]any{"X": 2, "Z": 1}, map[string]any{"X": 1, "Z": 2},
		}}},
		"goals": map[string]any{"AllowStartingSupplies": map[string]any{"Need": "deficit"}},
	}
	cells := []any{map[string]any{"x": 1, "z": 2}, map[string]any{"x": 2, "z": 1}}
	if err := AuditStartingSupplies(active, cells); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, mutation := range []struct {
		field string
		value any
	}{{"Initialized", false}, {"Pending", []any{}}, {"Pending", []any{map[string]any{"X": 99, "Z": 99}}}} {
		changed := deepCopyRoutineAny(active).(map[string]any)
		review, _ := AsMap(changed["review"])
		history, _ := AsMap(review["StartingSupplies"])
		history[mutation.field] = mutation.value
		if err := AuditStartingSupplies(changed, cells); err == nil {
			t.Fatalf("expected an error for field %s", mutation.field)
		}
	}
	review, _ := AsMap(active["review"])
	history, _ := AsMap(review["StartingSupplies"])
	history["Pending"] = nil
	goals, _ := AsMap(active["goals"])
	goal, _ := AsMap(goals["AllowStartingSupplies"])
	goal["Need"] = "recovered"
	if err := AuditStartingSupplies(active, []any{}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := AuditStartingSupplies(active, cells); err == nil {
		t.Fatal("expected an error once cells are non-empty but the goal says recovered")
	}
}

func TestComfortAcceptanceRequiresCurrentAccessibleFacilitiesAndUseHistory(t *testing.T) {
	active := map[string]any{
		"review": map[string]any{"Tick": 100, "Comfort": map[string]any{
			"Dining":     map[string]any{"Facility": "chair", "Tick": 90},
			"Recreation": map[string]any{"Facility": "hoop", "Tick": 100},
		}},
		"goals": map[string]any{"EnsureComfort": map[string]any{"Need": "recovered", "Status": "satisfied"}},
	}
	native := map[string]any{
		"people":     []any{"pawn"},
		"dining":     []any{map[string]any{"id": "chair", "accessibleTo": []any{"pawn"}}},
		"recreation": []any{map[string]any{"id": "hoop", "accessibleTo": []any{"pawn"}}},
	}
	if _, err := AuditComfortUse(active, native); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, mutation := range []struct {
		field string
		value any
	}{{"Facility", ""}, {"Facility", "replacement"}, {"Tick", 0}, {"Tick", 101}} {
		changed := deepCopyRoutineAny(active).(map[string]any)
		review, _ := AsMap(changed["review"])
		comfort, _ := AsMap(review["Comfort"])
		dining, _ := AsMap(comfort["Dining"])
		dining[mutation.field] = mutation.value
		if _, err := AuditComfortUse(changed, native); err == nil {
			t.Fatalf("expected an error for field %s", mutation.field)
		}
	}
	for _, kind := range []string{"dining", "recreation"} {
		changed := deepCopyRoutineAny(native).(map[string]any)
		facilities := AsSlice(changed[kind])
		facility, _ := AsMap(facilities[0])
		facility["accessibleTo"] = []any{}
		if _, err := AuditComfortUse(active, changed); err == nil {
			t.Fatalf("expected an error once %s accessibility is lost", kind)
		}
	}
	native["people"] = []any{}
	if _, err := AuditComfortUse(active, native); err == nil {
		t.Fatal("expected an error for no eligible colonists")
	}
}

func TestShellGeometryRequiresCompletePerimeterAndSouthDoor(t *testing.T) {
	plan := map[string]any{}
	var actions []any
	for x := 10; x <= 18; x++ {
		for z := 20; z <= 28; z++ {
			if x != 10 && x != 18 && z != 20 && z != 28 {
				continue
			}
			defName := "Wall"
			if x == 14 && z == 20 {
				defName = "Door"
			}
			actions = append(actions, map[string]any{"building": map[string]any{"x": x, "z": z, "stuff": "WoodLog", "defName": defName}})
		}
	}
	plan["actions"] = actions
	interior, err := ShellGeometry(plan)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	wantInterior := map[shellCell]bool{}
	for i := 11; i <= 17; i++ {
		for j := 21; j <= 27; j++ {
			wantInterior[shellCell{i, j}] = true
		}
	}
	if !shellCellSetsEqual(interior, wantInterior) {
		t.Fatalf("unexpected interior: %v", interior)
	}
	for _, mutation := range []string{"missing", "duplicate", "interior", "door", "stuff"} {
		changed := deepCopyRoutineAny(plan).(map[string]any)
		acts := AsSlice(changed["actions"])
		acts = append([]any(nil), acts...)
		switch mutation {
		case "missing":
			acts = acts[:len(acts)-1]
		case "duplicate":
			acts[len(acts)-1] = acts[0]
		case "interior":
			first, _ := AsMap(acts[0])
			building, _ := AsMap(first["building"])
			building["x"], building["z"] = 14, 24
		case "door":
			first, _ := AsMap(acts[0])
			building, _ := AsMap(first["building"])
			building["defName"] = "Door"
		case "stuff":
			first, _ := AsMap(acts[0])
			building, _ := AsMap(first["building"])
			building["stuff"] = "Steel"
		}
		changed["actions"] = acts
		if _, err := ShellGeometry(changed); err == nil {
			t.Fatalf("expected an error for mutation %s", mutation)
		}
	}
}

func TestOperationRetentionRejectsHistoryLossAndDuplicatePages(t *testing.T) {
	var history []map[string]any
	if err := AppendOperationHistory(&history, []map[string]any{{"Sequence": 24}, {"Sequence": 25}}, 23); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := AppendOperationHistory(&history, nil, 23); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := AppendOperationHistory(&history, []map[string]any{{"Sequence": 26}}, 23); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, batch := range [][]map[string]any{
		{{"Sequence": 26}},
		{{"Sequence": 28}},
		{{"Sequence": 28}, {"Sequence": 27}},
	} {
		if err := AppendOperationHistory(&history, batch, 23); err == nil {
			t.Fatalf("expected an error for batch %v", batch)
		}
		if len(history) != 3 {
			t.Fatalf("history must remain unchanged after a rejected batch, got %v", history)
		}
	}
}

func TestNativeOperationFileOrdersEventsAndPreservesGapCheck(t *testing.T) {
	path := filepath.Join(t.TempDir(), "events.jsonl")
	if err := os.WriteFile(path, []byte("{\"Sequence\":25}\n{\"Sequence\":23}\n{\"Sequence\":24}\n"), 0o644); err != nil {
		t.Fatalf("write file: %v", err)
	}
	rows, err := ReadOperationHistory(path, 23)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(rows) != 2 || int(AsNumber(rows[0]["Sequence"])) != 24 || int(AsNumber(rows[1]["Sequence"])) != 25 {
		t.Fatalf("unexpected rows: %v", rows)
	}
	if err := os.WriteFile(path, []byte("{\"Sequence\":25}\n"), 0o644); err != nil {
		t.Fatalf("write file: %v", err)
	}
	if _, err := ReadOperationHistory(path, 23); err == nil {
		t.Fatal("expected an error for a gap")
	}
}

func TestRoutineMedicalEvidencePreservesCareAndUnknowns(t *testing.T) {
	pawn := map[string]any{"dead": false, "downed": false, "health": map[string]any{"bleeding": false, "needsTend": true}}
	reply := map[string]any{"observed": map[string]any{"colonists": map[string]any{
		"pawns": []any{pawn},
		"completeness": map[string]any{
			"page": map[string]any{"complete": true}, "matched": "1", "returned": "1", "filtered": "0", "unreadable": "0",
		},
	}}}
	assertNeed := func(want string) {
		t.Helper()
		got, err := MedicalNeed(reply)
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if got != want {
			t.Fatalf("expected %s, got %s", want, got)
		}
	}
	assertNeed("deficit")
	health, _ := AsMap(pawn["health"])
	health["needsTend"] = false
	assertNeed("recovered")
	delete(health, "needsTend")
	assertNeed("unknown")
	pawn["dead"] = true
	assertNeed("recovered")
	observed, _ := AsMap(reply["observed"])
	colonists, _ := AsMap(observed["colonists"])
	completeness, _ := AsMap(colonists["completeness"])
	completeness["filtered"] = "1"
	assertNeed("unknown")
}

func constructionCensus() map[string]any {
	return map[string]any{"page": map[string]any{"complete": true}, "matched": "0", "returned": "0", "filtered": "0", "unreadable": "0"}
}

func TestConstructionStartAcceptsOmittedEmptyProtobufHostiles(t *testing.T) {
	reply := map[string]any{"observed": map[string]any{
		"colonists": map[string]any{"pawns": []any{}, "completeness": constructionCensus()},
		"threats":   map[string]any{"completeness": constructionCensus()},
	}}
	if err := AssertConstructionStart(reply); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, field := range []string{"matched", "returned", "filtered", "unreadable"} {
		changed := deepCopyRoutineAny(reply).(map[string]any)
		observed, _ := AsMap(changed["observed"])
		threats, _ := AsMap(observed["threats"])
		completeness, _ := AsMap(threats["completeness"])
		completeness[field] = "1"
		if err := AssertConstructionStart(changed); err == nil {
			t.Fatalf("expected an error for field %s", field)
		}
	}
	observed, _ := AsMap(reply["observed"])
	threats, _ := AsMap(observed["threats"])
	threats["hostiles"] = []any{map[string]any{"pawn": map[string]any{}}}
	if err := AssertConstructionStart(reply); err == nil {
		t.Fatal("expected an error for a starting hostile")
	}
}

func TestConstructionStartDistinguishesNonhostileWildlifeFromHunters(t *testing.T) {
	census := constructionCensus()
	countedCensus := deepCopyRoutineAny(census).(map[string]any)
	countedCensus["matched"] = "1"
	countedCensus["returned"] = "1"
	reply := map[string]any{"observed": map[string]any{
		"colonists": map[string]any{"pawns": []any{}, "completeness": census},
		"threats": map[string]any{
			"completeness":      countedCensus,
			"wildPredatorsNear": []any{map[string]any{"pawn": map[string]any{"hostile": false}}},
		},
	}}
	if err := AssertConstructionStart(reply); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, hostile := range []any{true, nil} {
		changed := deepCopyRoutineAny(reply).(map[string]any)
		observed, _ := AsMap(changed["observed"])
		threats, _ := AsMap(observed["threats"])
		near := AsSlice(threats["wildPredatorsNear"])
		row, _ := AsMap(near[0])
		pawn, _ := AsMap(row["pawn"])
		pawn["hostile"] = hostile
		if err := AssertConstructionStart(changed); err == nil {
			t.Fatalf("expected an error for hostile=%v", hostile)
		}
	}
	observed, _ := AsMap(reply["observed"])
	threats, _ := AsMap(observed["threats"])
	threats["huntingPredators"] = threats["wildPredatorsNear"]
	delete(threats, "wildPredatorsNear")
	if err := AssertConstructionStart(reply); err == nil {
		t.Fatal("expected an error once the incidental observation is reclassified as a hunting predator")
	}
}

func routineTraceFixture(names ...string) ([]map[string]any, map[string][]string) {
	caps := map[string][]string{}
	rows := make([]map[string]any, len(names))
	for i, name := range names {
		caps[itoa(i)] = []string{name}
		rows[i] = map[string]any{"Sequence": i + 1, "OperationId": itoa(i), "CapabilityId": itoa(i)}
	}
	return rows, caps
}

func itoa(i int) string {
	digits := "0123456789"
	if i == 0 {
		return "0"
	}
	s := ""
	for i > 0 {
		s = string(digits[i%10]) + s
		i /= 10
	}
	return s
}

func TestRoutineTraceRequiresAttributedNativeReads(t *testing.T) {
	names := []string{"rimgovernor/observations_read_colony_facts", "rimgovernor/observations_read_status"}
	rows, caps := routineTraceFixture(names...)
	got, err := AuditRoutine(rows, 0, caps, false)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(got) != 2 || got[0] != names[0] || got[1] != names[1] {
		t.Fatalf("unexpected names: %v", got)
	}
	if _, err := AuditRoutine(rows[1:], 0, caps, false); err == nil {
		t.Fatal("expected an error for a truncated trace")
	}
	if _, err := AuditRoutine(append(append([]map[string]any{}, rows...), rows[len(rows)-1]), 0, caps, false); err == nil {
		t.Fatal("expected an error for a duplicated-sequence trace")
	}
	if _, err := AuditRoutine(rows, 0, caps, true); err == nil {
		t.Fatal("expected an error requiring observations_read_colony_facts on restart")
	}
	for _, forbidden := range []string{"rimgovernor/authority_control", "rimgovernor/clock_start", "rimgovernor/operations_execute", "home/supervised_play"} {
		changed := map[string][]string{}
		for k, v := range caps {
			changed[k] = v
		}
		changed["0"] = []string{forbidden}
		if _, err := AuditRoutine(rows, 0, changed, true); err == nil {
			t.Fatalf("expected an error for forbidden capability %s on restart", forbidden)
		}
	}
}

func TestResourceRuleAuditAllowsClockButRejectsConstruction(t *testing.T) {
	plan := map[string]any{"actions": []any{map[string]any{"progress": map[string]any{"stage": "pending", "attempt": "0"}}}}
	names := []string{"rimgovernor/placement_preview", "rimgovernor/clock_start", "rimgovernor/placement_preview", "rimgovernor/clock_renew"}
	if err := AuditResourceRules(plan, names); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := AuditResourceRules(plan, append(append([]string{}, names...), clockExecute)); err == nil {
		t.Fatal("expected an error once operations_execute appears")
	}
	if err := AuditResourceRules(plan, names[:2]); err == nil {
		t.Fatal("expected an error for fewer than two placement previews")
	}
	for _, changed := range []map[string]any{
		{"actions": []any{}},
		{"actions": []any{map[string]any{"progress": map[string]any{"stage": "pending", "attempt": "1"}}}},
		{"actions": []any{map[string]any{"progress": map[string]any{"stage": "completed", "attempt": "0"}}}},
	} {
		if err := AuditResourceRules(changed, names); err == nil {
			t.Fatalf("expected an error for plan %v", changed)
		}
	}
}

func TestDevelopmentAuditKeepsPlayerCapacityAndKnownDeficits(t *testing.T) {
	review := map[string]any{"Snapshot": map[string]any{"Plan": "root"}, "Tick": 10, "Development": map[string]any{
		"Snapshot": map[string]any{"Plan": "root"}, "Tick": 10, "Workers": 3, "Capacity": 2,
		"Committed": []any{"player-project"}, "Rows": []any{
			map[string]any{"Goal": "EnsureBasicDefense", "Deficit": 1.0, "Score": 100.0,
				"WaitingSince": 10, "Selected": true, "Committed": false, "Reason": ""},
		},
	}}
	if _, err := AuditDevelopment(review, 3, 2); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	development, _ := AsMap(review["Development"])
	rows := AsSlice(development["Rows"])
	rows = append(rows, map[string]any{"Goal": "MaintainEquipment", "Deficit": 1.0, "Score": 100.0,
		"WaitingSince": 10, "Selected": false, "Committed": false, "Reason": "method_unavailable"})
	development["Rows"] = rows
	if _, err := AuditDevelopment(review, 3, 2); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, mutation := range []struct {
		field string
		value any
	}{{"Workers", nil}, {"Committed", []any{}}, {"Capacity", 3}} {
		changed := deepCopyRoutineAny(review).(map[string]any)
		changedDevelopment, _ := AsMap(changed["Development"])
		changedDevelopment[mutation.field] = mutation.value
		if _, err := AuditDevelopment(changed, 3, 2); err == nil {
			t.Fatalf("expected an error for field %s", mutation.field)
		}
	}
	changed := deepCopyRoutineAny(review).(map[string]any)
	changedDevelopment, _ := AsMap(changed["Development"])
	changedRows := AsSlice(changedDevelopment["Rows"])
	firstRow, _ := AsMap(changedRows[0])
	firstRow["Deficit"] = nil
	if _, err := AuditDevelopment(changed, 3, 2); err == nil {
		t.Fatal("expected an error once a selected row loses its deficit")
	}
}

func TestExpansionDevelopmentAuditUsesConfiguredCapacity(t *testing.T) {
	review := map[string]any{"Snapshot": map[string]any{"Plan": "root"}, "Tick": 10, "Development": map[string]any{
		"Snapshot": map[string]any{"Plan": "root"}, "Tick": 10, "Workers": 3, "Capacity": 3,
		"Committed": []any{"player-project"}, "Rows": []any{
			map[string]any{"Goal": "EnsureExpansion", "Deficit": .25, "Score": 25.0,
				"WaitingSince": 10, "Selected": true, "Committed": false, "Reason": ""},
		},
	}}
	if _, err := AuditDevelopment(review, 3, 3); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if _, err := AuditDevelopment(review, 3, 2); err == nil {
		t.Fatal("expected an error once the default project limit no longer matches the fixture's capacity")
	}
}
