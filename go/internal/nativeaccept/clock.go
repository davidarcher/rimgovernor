package nativeaccept

import (
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"path/filepath"

	_ "modernc.org/sqlite"
)

var clockReads = map[string]bool{
	"rimgovernor/lifecycle_read_identity":     true,
	"rimgovernor/observations_read_status":    true,
	"rimgovernor/observations_get_cells":      true,
	"rimgovernor/observations_list_buildings": true,
	"rimgovernor/authority_read_status":       true,
	"rimgovernor/receipts_lookup":             true,
	"rimgovernor/receipts_observe_progress":   true,
}

var clockDiagnostics = map[string]bool{"rimbridge/list_operation_events": true, "rimbridge/list_capabilities": true}

const clockControl = "rimgovernor/authority_control"
const clockExecute = "rimgovernor/operations_execute"

var clockClocks = map[string]bool{
	"rimgovernor/clock_read_status":  true,
	"rimgovernor/clock_read_events":  true,
	"rimgovernor/clock_read_attempt": true,
	"rimgovernor/clock_pause":        true,
}

// ClockAudit asserts a native operation-event history is gapless from baseline and
// every attributed event belongs to the allowed capability set for the Go-owned
// supervised clock, then returns the ordered operation names, mirroring
// native_go_clock_acceptance.py's audit(): a disabled restart must never expose
// clock_start/clock_renew/authority_control/operations_execute, and a running
// (non-restart) session must show at least one clock_start, clock_pause, and
// operations_execute, plus observations_read_colony_facts when routine reviews
// are enabled.
func ClockAudit(events []map[string]any, baseline int, capabilities map[string][]string, restart, routineReviews bool) ([]string, error) {
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
	for name := range clockClocks {
		allowed[name] = true
	}
	allowed["rimgovernor/observations_list_pawns"] = true
	if routineReviews && !restart {
		allowed["rimgovernor/observations_read_colony_facts"] = true
	}
	if !restart {
		allowed[clockControl] = true
		allowed[clockExecute] = true
		allowed["rimgovernor/operations_preview"] = true
		allowed["rimgovernor/placement_preview"] = true
		allowed["rimgovernor/clock_start"] = true
		allowed["rimgovernor/clock_renew"] = true
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
	if !Contains(names, "rimgovernor/clock_read_events") {
		return nil, fmt.Errorf("clock trace never observed clock_read_events")
	}
	if !restart {
		if !Contains(names, "rimgovernor/clock_start") || !Contains(names, "rimgovernor/clock_pause") {
			return nil, fmt.Errorf("running clock trace must show both clock_start and clock_pause")
		}
		executeCount := 0
		for _, name := range names {
			if name == clockExecute {
				executeCount++
			}
		}
		if executeCount < 1 {
			return nil, fmt.Errorf("running clock trace must show at least one operations_execute")
		}
		if routineReviews && !Contains(names, "rimgovernor/observations_read_colony_facts") {
			return nil, fmt.Errorf("routine-review clock trace must show observations_read_colony_facts")
		}
	}
	return names, nil
}

func sortBySequence(rows []map[string]any) {
	for i := 1; i < len(rows); i++ {
		for j := i; j > 0 && AsNumber(rows[j]["Sequence"]) < AsNumber(rows[j-1]["Sequence"]); j-- {
			rows[j], rows[j-1] = rows[j-1], rows[j]
		}
	}
}

// InterruptedByLetter asserts a Go-supervised clock's event stream shows exactly
// one real LetterStack interruption for the scheduled letter, mirroring
// native_go_clock_acceptance.py's interrupted_by_letter().
func InterruptedByLetter(events []map[string]any, letterID string) (map[string]any, error) {
	var matching []map[string]any
	for _, event := range events {
		stop, _ := AsMap(event["stopped"])
		pause, _ := AsMap(stop["pause"])
		letter, _ := AsMap(pause["letter"])
		if AsString(stop["reason"]) == "STOP_REASON_LETTER_PAUSE" && AsString(letter["id"]) == letterID {
			matching = append(matching, event)
		}
	}
	if len(matching) != 1 {
		return nil, fmt.Errorf("missing real LetterStack interruption for the scheduled letter %s", letterID)
	}
	return matching[0], nil
}

func explicitFalse(v any) bool {
	b, ok := AsBool(v)
	return ok && !b
}

// RequireHealthyColonists asserts a fresh observations_list_pawns reply is a
// complete, fully-matched census of colonists with none dead, downed, bleeding,
// or needing tend, mirroring native_go_clock_acceptance.py's
// require_healthy_colonists(): a missing health fact is never treated as healthy,
// only an explicit false is.
func RequireHealthyColonists(reply map[string]any) error {
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return err
	}
	colony, _ := AsMap(observed["colonists"])
	completeness, _ := AsMap(colony["completeness"])
	page, _ := AsMap(completeness["page"])
	if complete, ok := AsBool(page["complete"]); !ok || !complete {
		return fmt.Errorf("colonists page is not complete")
	}
	if numberOrDefault(completeness, "filtered", -1) != 0 {
		return fmt.Errorf("colonists completeness filtered is not zero")
	}
	if numberOrDefault(completeness, "unreadable", -1) != 0 {
		return fmt.Errorf("colonists completeness unreadable is not zero")
	}
	pawns := AsSlice(colony["pawns"])
	if len(pawns) == 0 {
		return fmt.Errorf("no colonists returned")
	}
	returned := AsNumber(completeness["returned"])
	matched := AsNumber(completeness["matched"])
	if float64(len(pawns)) != returned || returned != matched {
		return fmt.Errorf("colonist count does not match completeness returned/matched")
	}
	for _, raw := range pawns {
		pawn, _ := AsMap(raw)
		health, _ := AsMap(pawn["health"])
		var blocked []string
		for _, field := range []struct {
			name  string
			value any
		}{
			{"dead", pawn["dead"]}, {"downed", pawn["downed"]},
			{"bleeding", health["bleeding"]}, {"needsTend", health["needsTend"]},
		} {
			if !explicitFalse(field.value) {
				blocked = append(blocked, field.name)
			}
		}
		if len(blocked) > 0 {
			pawnRef, _ := AsMap(pawn["pawn"])
			return fmt.Errorf("healthy-clock fixture prerequisite failed for %s: %v", AsString(pawnRef["id"]), blocked)
		}
	}
	return nil
}

func numberOrDefault(m map[string]any, key string, def float64) float64 {
	if v, present := m[key]; present {
		return AsNumber(v)
	}
	return def
}

var routineNeeds = []string{
	"ConfirmColonyNames", "ActiveCombat", "CriticalMedical", "RestoreWorkers", "AllowStartingSupplies",
	"EnsureWorkAssignments", "EnsureFoodSupply", "EnsureInitialShelter", "EnsureTemperatureSafety",
	"EnsureCooking", "EnsureBasicPower", "EnsureFoodStorage", "EnsureBasicDefense", "MaintainWood",
	"MaintainMedicalCare", "EnsureComfort", "EnsureExpansion", "MaintainEquipment",
	"MaintainFireSafety", "SecureSupplies", "MaintainEssentialRepairs", "MaintainCleanFacilities",
	"MaintainMedicalReserves", "MaintainAnimalContainment", "MaintainAnimalFeed", "MaintainSleeping",
	"MaintainHomeCoverage", "MaintainStoneShell",
}

func truthyAny(v any) bool {
	switch t := v.(type) {
	case nil:
		return false
	case bool:
		return t
	case float64:
		return t != 0
	case int:
		return t != 0
	case string:
		return t != ""
	case []any:
		return len(t) > 0
	case map[string]any:
		return len(t) > 0
	default:
		return true
	}
}

// RoutineEvidence asserts a Go-owned routine review's durable SQLite state reflects
// exactly the fixed native goal set (plus any mood/disaster goals its own snapshot
// declares), that every bound goal is autopilot-sourced, scoped to the review's own
// snapshot, no newer than the review's own tick, and (while disabled) left
// invalidated or cancelled, mirroring native_go_clock_acceptance.py's
// routine_evidence(). It never treats a bound goal it cannot read, or a review that
// silently created methods when none were expected, as a pass.
func RoutineEvidence(databasePath string, identity map[string]any, enabled bool, expectedFoodNeed *string, allowMethods bool) (map[string]any, error) {
	uri := "file:" + filepath.ToSlash(databasePath) + "?mode=ro"
	db, err := sql.Open("sqlite", uri)
	if err != nil {
		return nil, fmt.Errorf("open routine review database: %w", err)
	}
	defer db.Close()

	var payload string
	if err := db.QueryRow("SELECT payload FROM routine_review WHERE singleton=1").Scan(&payload); err != nil {
		return nil, fmt.Errorf("no durable routine review: %w", err)
	}
	var review map[string]any
	if err := json.Unmarshal([]byte(payload), &review); err != nil {
		return nil, fmt.Errorf("invalid routine review payload: %w", err)
	}
	if AsNumber(review["Revision"]) <= 0 {
		return nil, fmt.Errorf("routine review revision must be positive")
	}
	reviewEnabled, ok := AsBool(review["Enabled"])
	if !ok || reviewEnabled != enabled {
		return nil, fmt.Errorf("routine review enabled flag does not match")
	}
	scope, _ := AsMap(review["Snapshot"])
	if AsString(scope["Colony"]) != AsString(identity["colonyId"]) ||
		AsString(scope["Load"]) != AsString(identity["loadToken"]) ||
		!DeepEqual(scope["Map"], identity["mapId"]) {
		return nil, fmt.Errorf("routine review scope does not match identity")
	}
	bindings := AsSlice(review["Goals"])

	expected := map[string]bool{}
	for _, name := range routineNeeds {
		expected[name] = true
	}
	moodStates := []any{}
	if moodBlock, ok := AsMap(review["Mood"]); ok {
		moodStates = AsSlice(moodBlock["States"])
	}
	if len(moodStates) > 256 {
		return nil, fmt.Errorf("routine review declares more than 256 mood states")
	}
	seenMoodPawns := map[string]bool{}
	for _, raw := range moodStates {
		state, _ := AsMap(raw)
		pawn, _ := AsMap(state["Pawn"])
		id := AsString(pawn["ID"])
		if seenMoodPawns[id] {
			return nil, fmt.Errorf("duplicate mood pawn id %s", id)
		}
		seenMoodPawns[id] = true
		if len([]byte(id)) <= 210 {
			expected["EnsureMood-"+id] = true
		} else {
			sum := sha256.Sum256([]byte(id))
			expected["EnsureMoodHash-"+hex.EncodeToString(sum[:])[:32]] = true
		}
	}
	if truthyAny(review["Disaster"]) {
		expected["RecoverDisasterServices"] = true
	}
	if len(bindings) != len(expected) {
		return nil, fmt.Errorf("routine review goal count does not match the expected native set")
	}
	bindingNeeds := map[string]bool{}
	for _, raw := range bindings {
		binding, _ := AsMap(raw)
		bindingNeeds[AsString(binding["Need"])] = true
	}
	if len(bindingNeeds) != len(expected) {
		return nil, fmt.Errorf("routine review goal set does not match the expected native set")
	}
	for name := range expected {
		if !bindingNeeds[name] {
			return nil, fmt.Errorf("routine review goal set does not match the expected native set")
		}
	}

	goals := map[string]map[string]any{}
	for _, raw := range bindings {
		binding, _ := AsMap(raw)
		var goalPayload string
		if err := db.QueryRow("SELECT payload FROM goals WHERE id=?", AsString(binding["Goal"])).Scan(&goalPayload); err != nil {
			return nil, fmt.Errorf("missing bound goal %s: %w", AsString(binding["Goal"]), err)
		}
		var goal map[string]any
		if err := json.Unmarshal([]byte(goalPayload), &goal); err != nil {
			return nil, fmt.Errorf("invalid goal payload: %w", err)
		}
		if AsString(goal["Source"]) != "autopilot" {
			return nil, fmt.Errorf("goal %s is not autopilot-sourced", AsString(binding["Goal"]))
		}
		if !DeepEqual(goal["Snapshot"], scope) {
			return nil, fmt.Errorf("goal %s snapshot does not match the review's own scope", AsString(binding["Goal"]))
		}
		if AsNumber(goal["Tick"]) > AsNumber(review["Tick"]) {
			return nil, fmt.Errorf("goal %s tick is newer than the review's own tick", AsString(binding["Goal"]))
		}
		if !enabled {
			status := AsString(goal["Status"])
			if status != "invalidated" && status != "cancelled" {
				return nil, fmt.Errorf("goal %s must be invalidated or cancelled while disabled, found %q", AsString(binding["Goal"]), status)
			}
		}
		goals[AsString(binding["Need"])] = goal
	}
	if expectedFoodNeed != nil {
		foodGoal := goals["EnsureFoodSupply"]
		if AsString(foodGoal["Need"]) != *expectedFoodNeed {
			return nil, fmt.Errorf("food need differs from reference forecast")
		}
	}
	if !allowMethods {
		var count int
		if err := db.QueryRow("SELECT count(*) FROM goal_methods").Scan(&count); err != nil {
			return nil, fmt.Errorf("read goal_methods count: %w", err)
		}
		if count != 0 {
			return nil, fmt.Errorf("review unexpectedly created methods")
		}
	}
	return map[string]any{"review": review, "goals": goals}, nil
}
