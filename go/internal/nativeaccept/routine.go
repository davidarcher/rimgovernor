package nativeaccept

import (
	"encoding/json"
	"fmt"
	"math"
	"os"
	"sort"
	"strings"
)

// HTTPGet issues one Go-service HTTP request and returns its decoded JSON body.
type HTTPGet func(method, path string) (map[string]any, error)

// AssertRoutineRunning asserts the Go-owned supervised clock is still in automate
// mode, mirroring native_go_routine_acceptance.py's assert_routine_running(): a
// non-automate state is always an interruption, and the player clock is fetched
// only to enrich that error, never to justify continuing.
func AssertRoutineRunning(http HTTPGet) error {
	state, err := http("GET", "/api/state")
	if err != nil {
		return err
	}
	if AsString(state["mode"]) == "automate" {
		return nil
	}
	clock, err := http("GET", "/api/player/clock")
	if err != nil {
		return err
	}
	return fmt.Errorf("routine acceptance interrupted: state=%v, clock=%v", state, clock)
}

// AuditMedicalCare asserts a Go routine review's durable MedicalCare census and
// MaintainMedicalCare goal reproduce a reference medical-care evaluation exactly,
// mirroring native_go_routine_acceptance.py's audit_medical_care(). This does not
// port medical_care_reference()/care_state() themselves, since those compute the
// actual medical-triage facts from raw pawn data -- production runtime logic
// (rimgovernor.medical_management), out of this migration's scope; callers supply
// the reference dict directly.
func AuditMedicalCare(active, expected map[string]any) error {
	review, _ := AsMap(active["review"])
	history, _ := AsMap(review["MedicalCare"])
	if censusKnown, ok := AsBool(history["CensusKnown"]); !ok || !censusKnown {
		return fmt.Errorf("medical care census is not known")
	}
	if !DeepEqual(AsSlice(history["Patients"]), expected["patients"]) {
		return fmt.Errorf("medical care patients do not match the reference evaluation")
	}
	if !DeepEqual(AsSlice(history["Unknown"]), expected["unknown"]) {
		return fmt.Errorf("medical care unknown pawns do not match the reference evaluation")
	}
	goals, _ := AsMap(active["goals"])
	goal, _ := AsMap(goals["MaintainMedicalCare"])
	if AsString(goal["Need"]) != AsString(expected["need"]) {
		return fmt.Errorf("medical care goal need does not match the reference evaluation")
	}
	if AsNumber(goal["Priority"]) != 2 {
		return fmt.Errorf("medical care goal priority must be 2")
	}
	return nil
}

// AuditStartingSupplies asserts a Go routine review's durable StartingSupplies
// census matches the native original forbidden-supply cells exactly (once sorted
// into (Z,X) native order) and its AllowStartingSupplies goal need reflects
// whether any such cells remain, mirroring native_go_routine_acceptance.py's
// audit_starting_supplies().
func AuditStartingSupplies(active map[string]any, cells []any) error {
	review, _ := AsMap(active["review"])
	history, _ := AsMap(review["StartingSupplies"])
	expected := make([]map[string]any, 0, len(cells))
	for _, raw := range cells {
		cell, ok := AsMap(raw)
		if !ok {
			return fmt.Errorf("starting supplies cell must be an object")
		}
		expected = append(expected, map[string]any{"X": cell["x"], "Z": cell["z"]})
	}
	sort.Slice(expected, func(i, j int) bool {
		zi, zj := AsNumber(expected[i]["Z"]), AsNumber(expected[j]["Z"])
		if zi != zj {
			return zi < zj
		}
		return AsNumber(expected[i]["X"]) < AsNumber(expected[j]["X"])
	})
	expectedAny := make([]any, len(expected))
	for i, cell := range expected {
		expectedAny[i] = cell
	}
	if initialized, ok := AsBool(history["Initialized"]); !ok || !initialized {
		return fmt.Errorf("starting supplies was not initialized")
	}
	if !DeepEqual(AsSlice(history["Pending"]), expectedAny) {
		return fmt.Errorf("starting supplies pending does not match the native original cells")
	}
	goals, _ := AsMap(active["goals"])
	goal, _ := AsMap(goals["AllowStartingSupplies"])
	wantNeed := "recovered"
	if len(cells) > 0 {
		wantNeed = "deficit"
	}
	if AsString(goal["Need"]) != wantNeed {
		return fmt.Errorf("starting supplies goal need does not match cell presence")
	}
	return nil
}

// AuditComfortUse asserts a recovered EnsureComfort goal's dining/recreation use
// proof still identifies a currently accessible native facility for every eligible
// colonist, mirroring native_go_routine_acceptance.py's audit_comfort_use(): a
// facility that no longer admits every eligible colonist, or whose proof no
// longer names an accessible facility, is never treated as a pass.
func AuditComfortUse(active, native map[string]any) (map[string]any, error) {
	goals, _ := AsMap(active["goals"])
	goal, _ := AsMap(goals["EnsureComfort"])
	if AsString(goal["Need"]) != "recovered" || AsString(goal["Status"]) != "satisfied" {
		return nil, fmt.Errorf("comfort goal must be recovered and satisfied")
	}
	review, _ := AsMap(active["review"])
	history, _ := AsMap(review["Comfort"])
	people := map[string]bool{}
	for _, raw := range AsSlice(native["people"]) {
		people[AsString(raw)] = true
	}
	if len(people) == 0 {
		return nil, fmt.Errorf("comfort acceptance requires actual eligible colonists")
	}
	reviewTick := AsNumber(review["Tick"])
	for _, kind := range []string{"Dining", "Recreation"} {
		proof, _ := AsMap(history[kind])
		facilityID := AsString(proof["Facility"])
		if facilityID == "" {
			return nil, fmt.Errorf("%s use proof has no facility", kind)
		}
		tick := AsNumber(proof["Tick"])
		if !(tick > 0 && tick <= reviewTick) {
			return nil, fmt.Errorf("%s use proof tick is out of bounds", kind)
		}
		facilities := AsSlice(native[strings.ToLower(kind)])
		accessible := map[string]bool{}
		for _, raw := range facilities {
			f, _ := AsMap(raw)
			for _, p := range AsSlice(f["accessibleTo"]) {
				accessible[AsString(p)] = true
			}
		}
		for p := range people {
			if !accessible[p] {
				return nil, fmt.Errorf("current %s comfort capacity lost", kind)
			}
		}
		matched := false
		for _, raw := range facilities {
			f, _ := AsMap(raw)
			if AsString(f["id"]) != facilityID {
				continue
			}
			for _, p := range AsSlice(f["accessibleTo"]) {
				if people[AsString(p)] {
					matched = true
					break
				}
			}
			if matched {
				break
			}
		}
		if !matched {
			return nil, fmt.Errorf("%s use proof no longer identifies an accessible native facility", kind)
		}
	}
	return history, nil
}

type shellCell = [2]int

// ShellGeometry asserts a starter shell plan places exactly 32 WoodLog buildings
// forming a complete 9x9 perimeter with its one door on the south wall's center
// cell, then returns the interior 7x7 cell set, mirroring
// native_go_routine_acceptance.py's shell_geometry().
func ShellGeometry(plan map[string]any) (map[shellCell]bool, error) {
	actions := AsSlice(plan["actions"])
	buildings := make([]map[string]any, 0, len(actions))
	for _, raw := range actions {
		action, _ := AsMap(raw)
		building, ok := AsMap(action["building"])
		if !ok {
			return nil, fmt.Errorf("shell action must carry a building")
		}
		buildings = append(buildings, building)
	}
	if len(buildings) != 32 {
		return nil, fmt.Errorf("shell requires exactly 32 buildings, found %d", len(buildings))
	}
	cells := map[shellCell]bool{}
	for _, b := range buildings {
		cells[shellCell{int(AsNumber(b["x"])), int(AsNumber(b["z"]))}] = true
	}
	if len(cells) != 32 {
		return nil, fmt.Errorf("shell buildings must occupy 32 distinct cells, found %d", len(cells))
	}
	minX, minZ := math.MaxInt, math.MaxInt
	for c := range cells {
		if c[0] < minX {
			minX = c[0]
		}
		if c[1] < minZ {
			minZ = c[1]
		}
	}
	want := map[shellCell]bool{}
	for i := minX; i < minX+9; i++ {
		for j := minZ; j < minZ+9; j++ {
			if i == minX || i == minX+8 || j == minZ || j == minZ+8 {
				want[shellCell{i, j}] = true
			}
		}
	}
	if !shellCellSetsEqual(cells, want) {
		return nil, fmt.Errorf("shell cells do not form a complete perimeter")
	}
	for _, b := range buildings {
		if AsString(b["stuff"]) != "WoodLog" {
			return nil, fmt.Errorf("shell building stuff must be WoodLog")
		}
		x, z := int(AsNumber(b["x"])), int(AsNumber(b["z"]))
		wantDef := "Wall"
		if x == minX+4 && z == minZ {
			wantDef = "Door"
		}
		if AsString(b["defName"]) != wantDef {
			return nil, fmt.Errorf("shell building at (%d,%d) must be %s", x, z, wantDef)
		}
	}
	interior := map[shellCell]bool{}
	for i := minX + 1; i < minX+8; i++ {
		for j := minZ + 1; j < minZ+8; j++ {
			interior[shellCell{i, j}] = true
		}
	}
	return interior, nil
}

func shellCellSetsEqual(a, b map[shellCell]bool) bool {
	if len(a) != len(b) {
		return false
	}
	for c := range a {
		if !b[c] {
			return false
		}
	}
	return true
}

// AppendOperationHistory extends a retained native operation-event history with a
// contiguous batch (never exceeding the scenario's 100000-row bound), mirroring
// native_go_routine_acceptance.py's append_operation_history(): a batch that skips
// or repeats a sequence number is always rejected, and rejection never mutates the
// retained history.
func AppendOperationHistory(history *[]map[string]any, batch []map[string]any, baseline int) error {
	cursor := baseline
	if len(*history) > 0 {
		cursor = int(AsNumber((*history)[len(*history)-1]["Sequence"]))
	}
	if len(*history)+len(batch) > 100000 {
		return fmt.Errorf("operation history exceeds scenario bound")
	}
	for i, row := range batch {
		want := cursor + 1 + i
		if got := int(AsNumber(row["Sequence"])); got != want {
			return fmt.Errorf("operation history gap or duplicate")
		}
	}
	*history = append(*history, batch...)
	return nil
}

// ReadOperationHistory reads a retained native operation-history JSONL file
// (never exceeding a 64MiB bound), keeps only rows newer than baseline, and
// appends them in sequence order via AppendOperationHistory, mirroring
// native_go_routine_acceptance.py's read_operation_history().
func ReadOperationHistory(path string, baseline int) ([]map[string]any, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if info.Size() > 64*1024*1024 {
		return nil, fmt.Errorf("native operation history exceeds bound")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var rows []map[string]any
	for _, line := range strings.Split(string(data), "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var row map[string]any
		if err := json.Unmarshal([]byte(line), &row); err != nil {
			return nil, fmt.Errorf("invalid operation history line: %w", err)
		}
		if AsNumber(row["Sequence"]) > float64(baseline) {
			rows = append(rows, row)
		}
	}
	sort.Slice(rows, func(i, j int) bool { return AsNumber(rows[i]["Sequence"]) < AsNumber(rows[j]["Sequence"]) })
	var history []map[string]any
	if err := AppendOperationHistory(&history, rows, baseline); err != nil {
		return nil, err
	}
	return history, nil
}

// MedicalNeed classifies a fresh observations_list_pawns reply's medical urgency
// from pure native health facts (dead/downed/bleeding/needsTend), mirroring
// native_go_routine_acceptance.py's medical_need(): an incomplete census or any
// missing/non-boolean health fact for a living pawn is always "unknown", never
// silently treated as healthy.
func MedicalNeed(reply map[string]any) (string, error) {
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return "", err
	}
	colony, _ := AsMap(observed["colonists"])
	census, _ := AsMap(colony["completeness"])
	rows := AsSlice(colony["pawns"])
	page, _ := AsMap(census["page"])
	complete, _ := AsBool(page["complete"])
	if !complete || numberOrDefault(census, "filtered", -1) != 0 || numberOrDefault(census, "unreadable", -1) != 0 {
		return "unknown", nil
	}
	if float64(len(rows)) != AsNumber(census["matched"]) || float64(len(rows)) != AsNumber(census["returned"]) {
		return "unknown", nil
	}
	needed := false
	for _, raw := range rows {
		pawn, _ := AsMap(raw)
		dead, deadOK := AsBool(pawn["dead"])
		if deadOK && dead {
			continue
		}
		health, _ := AsMap(pawn["health"])
		downed, downedOK := AsBool(pawn["downed"])
		bleeding, bleedingOK := AsBool(health["bleeding"])
		needsTend, needsTendOK := AsBool(health["needsTend"])
		if !deadOK || !downedOK || !bleedingOK || !needsTendOK {
			return "unknown", nil
		}
		if dead || downed || bleeding || needsTend {
			needed = true
		}
	}
	if needed {
		return "deficit", nil
	}
	return "recovered", nil
}

// AssertConstructionStart asserts a fresh scenario's starting colonists are all
// healthy and its native threat census shows no starting hostiles or hunting
// predators, with every incidental predator/downed observation explicitly marked
// non-hostile, mirroring native_go_routine_acceptance.py's
// assert_construction_start().
func AssertConstructionStart(reply map[string]any) error {
	need, err := MedicalNeed(reply)
	if err != nil {
		return err
	}
	if need != "recovered" {
		return fmt.Errorf("construction fixture requires healthy starting colonists")
	}
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return err
	}
	threats, _ := AsMap(observed["threats"])
	census, _ := AsMap(threats["completeness"])
	groups := []string{"hostiles", "huntingPredators", "ignoredHunters", "wildPredatorsNear", "downedNear"}
	count := 0
	for _, k := range groups {
		count += len(AsSlice(threats[k]))
	}
	page, _ := AsMap(census["page"])
	complete, _ := AsBool(page["complete"])
	if !complete || AsNumber(census["filtered"]) != 0 || AsNumber(census["unreadable"]) != 0 ||
		AsNumber(census["matched"]) != float64(count) || AsNumber(census["returned"]) != float64(count) {
		return fmt.Errorf("construction fixture requires a complete threat census")
	}
	if len(AsSlice(threats["hostiles"])) > 0 || len(AsSlice(threats["huntingPredators"])) > 0 {
		return fmt.Errorf("construction fixture requires no starting hostiles or hunting predators")
	}
	for _, key := range []string{"ignoredHunters", "wildPredatorsNear", "downedNear"} {
		for _, raw := range AsSlice(threats[key]) {
			row, _ := AsMap(raw)
			pawn, _ := AsMap(row["pawn"])
			hostile, ok := AsBool(pawn["hostile"])
			if !ok || hostile {
				return fmt.Errorf("construction fixture requires explicit non-hostile incidental observations")
			}
		}
	}
	return nil
}

// AuditRoutine asserts a native operation-event history is gapless from baseline
// and every attributed event belongs to the allowed capability set for the
// Go-owned routine reviewer, then returns the ordered operation names, mirroring
// native_go_routine_acceptance.py's audit_routine(): a running (non-restart)
// session must show observations_read_colony_facts, and a disabled restart must
// never expose authority/clock/execute/preview capabilities.
func AuditRoutine(events []map[string]any, baseline int, capabilities map[string][]string, restart bool) ([]string, error) {
	ordered := append([]map[string]any(nil), events...)
	sortBySequence(ordered)
	if len(ordered) == 0 {
		return nil, fmt.Errorf("incomplete SDK operation history")
	}
	for i, row := range ordered {
		want := baseline + 1 + i
		if got := int(AsNumber(row["Sequence"])); got != want {
			return nil, fmt.Errorf("incomplete SDK operation history: sequence %d, wanted %d", got, want)
		}
	}
	allowed := map[string]bool{}
	for name := range clockReads {
		allowed[name] = true
	}
	for name := range clockDiagnostics {
		allowed[name] = true
	}
	allowed["rimgovernor/observations_list_pawns"] = true
	for name := range clockClocks {
		allowed[name] = true
	}
	if !restart {
		allowed[clockControl] = true
		allowed[clockExecute] = true
		allowed["rimgovernor/operations_preview"] = true
		allowed["rimgovernor/placement_preview"] = true
		allowed["rimgovernor/clock_start"] = true
		allowed["rimgovernor/clock_renew"] = true
		allowed["rimgovernor/observations_read_colony_facts"] = true
		allowed["rimgovernor/observations_list_rooms"] = true
	}
	operations := map[string]string{}
	var operationOrder []string
	for _, row := range ordered {
		capability := AsString(row["CapabilityId"])
		if capability == "" {
			if AsString(row["OperationId"]) != "" {
				return nil, fmt.Errorf("operation event has no capability attribution")
			}
			continue
		}
		var selected []string
		for _, alias := range capabilities[capability] {
			if allowed[alias] {
				selected = append(selected, alias)
			}
		}
		if len(selected) != 1 {
			return nil, fmt.Errorf("unexpected operation: %v", capabilities[capability])
		}
		name := selected[0]
		operation := AsString(row["OperationId"])
		if existing, ok := operations[operation]; ok {
			if existing != name {
				return nil, fmt.Errorf("operation %s attributed to conflicting capabilities %q and %q", operation, existing, name)
			}
		} else {
			operations[operation] = name
			operationOrder = append(operationOrder, operation)
		}
	}
	names := make([]string, 0, len(operationOrder))
	for _, operation := range operationOrder {
		names = append(names, operations[operation])
	}
	if !restart && !Contains(names, "rimgovernor/observations_read_colony_facts") {
		return nil, fmt.Errorf("running routine trace must show observations_read_colony_facts")
	}
	return names, nil
}

// AuditResourceRules asserts a resource-policy plan's actions remain untouched
// (pending, zero attempts) while the routine reviewer previews placement
// repeatedly without ever executing, mirroring
// native_go_routine_acceptance.py's audit_resource_rules().
func AuditResourceRules(plan map[string]any, names []string) error {
	actions := AsSlice(plan["actions"])
	if len(actions) == 0 {
		return fmt.Errorf("resource rules plan has no actions")
	}
	for _, raw := range actions {
		action, _ := AsMap(raw)
		progress, _ := AsMap(action["progress"])
		if AsString(progress["stage"]) != "pending" || AsString(progress["attempt"]) != "0" {
			return fmt.Errorf("resource rules actions must remain pending with no attempts")
		}
	}
	if Contains(names, clockExecute) {
		return fmt.Errorf("resource rules trace must never execute")
	}
	count := 0
	for _, n := range names {
		if n == "rimgovernor/placement_preview" {
			count++
		}
	}
	if count < 2 {
		return fmt.Errorf("resource rules trace must preview placement at least twice")
	}
	return nil
}

var developmentGoalNames = map[string]bool{
	"MaintainWood": true, "EnsureBasicDefense": true, "EnsureComfort": true, "EnsureExpansion": true,
	"MaintainEquipment": true, "MaintainFireSafety": true, "SecureSupplies": true, "MaintainEssentialRepairs": true,
	"MaintainCleanFacilities": true, "MaintainMedicalReserves": true, "MaintainAnimalContainment": true,
	"MaintainAnimalFeed": true, "MaintainSleeping": true, "MaintainHomeCoverage": true, "MaintainStoneShell": true,
}

// AuditDevelopment asserts a Go routine review's Development block reflects the
// player's own snapshot/tick, the correct worker-derived optional capacity, one
// consumed slot for the accepted player project, and a well-formed, capacity-
// bounded set of native development rows (finite score, in-bounds waiting tick,
// and — for any selected row — a known deficit with no blocking reason or
// existing commitment), mirroring native_go_routine_acceptance.py's
// audit_development().
func AuditDevelopment(review map[string]any, workers, projectLimit int) (map[string]any, error) {
	development, _ := AsMap(review["Development"])
	if !DeepEqual(development["Snapshot"], review["Snapshot"]) || !DeepEqual(development["Tick"], review["Tick"]) {
		return nil, fmt.Errorf("development snapshot/tick does not match the review")
	}
	if int(AsNumber(development["Workers"])) != workers {
		return nil, fmt.Errorf("development workers does not match the observed worker count")
	}
	wantCapacity := projectLimit
	if workers < projectLimit {
		wantCapacity = workers
	}
	if int(AsNumber(development["Capacity"])) != wantCapacity {
		return nil, fmt.Errorf("development capacity does not match min(projectLimit, workers)")
	}
	if len(AsSlice(development["Committed"])) != 1 {
		return nil, fmt.Errorf("accepted player project must consume optional capacity")
	}
	rows := AsSlice(development["Rows"])
	seenGoals := map[string]bool{}
	for _, raw := range rows {
		row, _ := AsMap(raw)
		goal := AsString(row["Goal"])
		if seenGoals[goal] {
			return nil, fmt.Errorf("duplicate development goal %s", goal)
		}
		seenGoals[goal] = true
	}
	developmentTick := AsNumber(review["Tick"])
	selectedCount := 0
	for _, raw := range rows {
		row, _ := AsMap(raw)
		goal := AsString(row["Goal"])
		if !developmentGoalNames[goal] {
			return nil, fmt.Errorf("unexpected development goal %s", goal)
		}
		if row["Deficit"] != nil {
			d := AsNumber(row["Deficit"])
			if d < 0 || d > 1 {
				return nil, fmt.Errorf("development deficit out of bounds for %s", goal)
			}
		}
		score := AsNumber(row["Score"])
		if math.IsInf(score, 0) || math.IsNaN(score) {
			return nil, fmt.Errorf("development score must be finite for %s", goal)
		}
		waitingSince := AsNumber(row["WaitingSince"])
		if waitingSince < 0 || waitingSince > developmentTick {
			return nil, fmt.Errorf("development waitingSince out of bounds for %s", goal)
		}
		selected, _ := AsBool(row["Selected"])
		if selected {
			if row["Deficit"] == nil {
				return nil, fmt.Errorf("selected development row %s must have a known deficit", goal)
			}
			if AsString(row["Reason"]) != "" {
				return nil, fmt.Errorf("selected development row %s must have no blocking reason", goal)
			}
			committed, _ := AsBool(row["Committed"])
			if committed {
				return nil, fmt.Errorf("selected development row %s must not already be committed", goal)
			}
			selectedCount++
		}
	}
	limit := AsNumber(development["Capacity"]) - 1
	if limit < 0 {
		limit = 0
	}
	if float64(selectedCount) > limit {
		return nil, fmt.Errorf("selected development rows exceed available capacity")
	}
	return development, nil
}
