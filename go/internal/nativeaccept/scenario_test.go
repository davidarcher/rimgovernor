package nativeaccept

import (
	"context"
	"fmt"
	"strings"
	"testing"
)

func scenarioIdentity() map[string]any {
	return map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 0.0}
}

func scenarioContext(tick int) map[string]any {
	return map[string]any{"identity": scenarioIdentity(), "tick": float64(tick)}
}

func scenarioEpoch(owner string, epochID, startTick, lastTick, tickDeadline int) map[string]any {
	return map[string]any{
		"origin":    scenarioContext(startTick),
		"owner":     map[string]any{"controllerSessionId": owner, "epoch": float64(epochID)},
		"startTick": float64(startTick), "lastTick": float64(lastTick), "tickDeadline": float64(tickDeadline),
	}
}

func TestScenarioIntegerAcceptsNumberAndDecimalString(t *testing.T) {
	if v, err := scenarioInteger(float64(42)); err != nil || v != 42 {
		t.Fatalf("got %v, %v", v, err)
	}
	if v, err := scenarioInteger("42"); err != nil || v != 42 {
		t.Fatalf("got %v, %v", v, err)
	}
	if _, err := scenarioInteger("not-a-number"); err == nil {
		t.Fatal("expected an error for a non-numeric string")
	}
	if _, err := scenarioInteger(true); err == nil {
		t.Fatal("expected an error for a non-numeric type")
	}
}

func TestValidateScenarioContextRejectsMissingFields(t *testing.T) {
	if err := validateScenarioContext(scenarioContext(5)); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	bad := scenarioContext(5)
	bad["identity"].(map[string]any)["colonyId"] = ""
	if err := validateScenarioContext(bad); err == nil {
		t.Fatal("expected an error for an empty colonyId")
	}
}

func runningStatus(owner string, epochID, startTick, lastTick, tickDeadline int, newestCursor int) map[string]any {
	return map[string]any{
		"context": scenarioContext(lastTick), "actualPaused": false, "nativeTickBoundary": true,
		"newestCursor": float64(newestCursor),
		"running":      map[string]any{"epoch": scenarioEpoch(owner, epochID, startTick, lastTick, tickDeadline)},
	}
}

func stoppedStatus(owner string, epochID, startTick, lastTick, tickDeadline int, newestCursor int, reason string, paused bool) map[string]any {
	return map[string]any{
		"context": scenarioContext(lastTick), "actualPaused": paused, "nativeTickBoundary": true,
		"newestCursor": float64(newestCursor),
		"stopped": map[string]any{
			"epoch":  scenarioEpoch(owner, epochID, startTick, lastTick, tickDeadline),
			"reason": reason, "pauseVerified": true, "actualPaused": paused,
		},
	}
}

func TestProjectStatusRunning(t *testing.T) {
	got, err := projectStatus(runningStatus("owner-1", 1, 0, 5, 100, 0))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got["active"] != true || got["owner"] != "owner-1" || got["epoch"] != uint64(1) {
		t.Fatalf("unexpected projection: %#v", got)
	}
	if got["startTick"] != uint64(0) || got["lastTick"] != uint64(5) || got["tickDeadline"] != uint64(100) {
		t.Fatalf("unexpected ticks: %#v", got)
	}
}

func TestProjectStatusStoppedTickBudget(t *testing.T) {
	got, err := projectStatus(stoppedStatus("owner-1", 1, 0, 100, 100, 5, "STOP_REASON_TICK_BUDGET", true))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got["active"] != false || got["stopReason"] != "tick_budget" || got["pauseVerified"] != true {
		t.Fatalf("unexpected projection: %#v", got)
	}
}

func TestProjectStatusRejectsBadTickOrdering(t *testing.T) {
	status := runningStatus("owner-1", 1, 10, 5, 100, 0) // lastTick < startTick
	if _, err := projectStatus(status); err == nil {
		t.Fatal("expected an error for invalid tick ordering")
	}
}

func TestProjectStatusRejectsUnspecifiedStopReason(t *testing.T) {
	status := stoppedStatus("owner-1", 1, 0, 10, 100, 0, "STOP_REASON_UNSPECIFIED", true)
	if _, err := projectStatus(status); err == nil {
		t.Fatal("expected an error for an unspecified stop reason")
	}
}

func eventPage(identity map[string]any, newest int, events []map[string]any) map[string]any {
	return map[string]any{
		"context": map[string]any{"identity": identity, "tick": float64(newest)},
		"gap":     false, "lostCount": float64(0), "newestCursor": float64(newest), "nextCursor": float64(newest),
		"events": toAnySlice(events),
	}
}

func toAnySlice(rows []map[string]any) []any {
	out := make([]any, len(rows))
	for i, r := range rows {
		out[i] = r
	}
	return out
}

func letterPauseEvent(cursor, epochID int, owner, letterID string) map[string]any {
	return map[string]any{
		"cursor": float64(cursor), "context": scenarioContext(cursor),
		"owner":   map[string]any{"controllerSessionId": owner, "epoch": float64(epochID)},
		"stopped": map[string]any{"reason": "STOP_REASON_LETTER_PAUSE", "pause": map[string]any{"letter": map[string]any{"id": letterID}}},
	}
}

func TestProjectEventsAcceptsContiguousLetterPause(t *testing.T) {
	identity := scenarioIdentity()
	events := []map[string]any{letterPauseEvent(1, 1, "owner-1", "letter-1")}
	page := eventPage(identity, 1, events)
	page["nextCursor"] = float64(1)
	got, err := projectEvents(page, 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	rows := AsSlice(got["events"])
	if len(rows) != 1 {
		t.Fatalf("expected one event row, got %#v", rows)
	}
	row, _ := AsMap(rows[0])
	if AsString(row["kind"]) != "letter_pause" {
		t.Fatalf("unexpected kind: %#v", row)
	}
	eventDetail, _ := AsMap(row["event"])
	if AsString(eventDetail["letterId"]) != "letter-1" {
		t.Fatalf("unexpected letter id: %#v", eventDetail)
	}
}

func TestProjectEventsRejectsCursorGap(t *testing.T) {
	identity := scenarioIdentity()
	events := []map[string]any{letterPauseEvent(2, 1, "owner-1", "letter-1")} // should be cursor 1, not 2
	page := eventPage(identity, 2, events)
	if _, err := projectEvents(page, 0); err == nil {
		t.Fatal("expected an error for a cursor discontinuity")
	}
}

func TestProjectEventsRejectsReportedGap(t *testing.T) {
	page := eventPage(scenarioIdentity(), 0, nil)
	page["gap"] = true
	if _, err := projectEvents(page, 0); err == nil {
		t.Fatal("expected an error when the native reply reports a gap")
	}
}

// fakeWire builds a ScenarioClock.Wire double from a queue of canned replies keyed
// by method, so ScenarioClock method tests never need a live bridge connection.
type fakeWire struct {
	replies map[string][]map[string]any
	calls   []string
	// sent records every request by method so tests can assert wire shapes.
	sent map[string][]map[string]any
}

func (f *fakeWire) wire(_ context.Context, _, method string, request map[string]any) (map[string]any, error) {
	f.calls = append(f.calls, method)
	if f.sent == nil {
		f.sent = map[string][]map[string]any{}
	}
	f.sent[method] = append(f.sent[method], request)
	queue := f.replies[method]
	if len(queue) == 0 {
		panic("no canned reply for method " + method)
	}
	f.replies[method] = queue[1:]
	return queue[0], nil
}

func grantedReply(generation int) map[string]any {
	return map[string]any{"granted": map[string]any{
		"context":   map[string]any{"nativeGeneration": float64(generation)},
		"authority": map[string]any{"mode": "MODE_AUTO"},
	}}
}

func activeStatus(generation int) map[string]any {
	return map[string]any{"status": map[string]any{
		"context": map[string]any{"nativeGeneration": float64(generation)},
		"active":  map[string]any{"mode": "MODE_AUTO"},
	}}
}

func TestScenarioClockAcquireGrantsAutoAtCurrentGeneration(t *testing.T) {
	fw := &fakeWire{replies: map[string][]map[string]any{
		"authority_read_status": {{"status": map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "inactive": map[string]any{"reason": "REVOCATION_REASON_NONE"}}}},
		"authority_control":     {grantedReply(2)},
	}}
	clock := &ScenarioClock{Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}}
	grant, err := clock.Acquire(context.Background(), "acquire")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if GrantGeneration(grant) != 2 || GrantGeneration(clock.Grant) != 2 {
		t.Fatalf("unexpected grant: %#v / %#v", grant, clock.Grant)
	}
	sent := fw.sent["authority_control"]
	if len(sent) != 1 {
		t.Fatalf("expected one authority_control call, got %#v", sent)
	}
	setMode, _ := AsMap(sent[0]["setMode"])
	if AsString(setMode["expectedGeneration"]) != "1" || AsString(setMode["mode"]) != "MODE_AUTO" {
		t.Fatalf("expected SetMode(Auto) at generation 1, got %#v", sent[0])
	}
	if _, legacy := sent[0]["acquire"]; legacy {
		t.Fatalf("legacy acquire shape sent: %#v", sent[0])
	}
}

func TestScenarioClockRenewAuthorityRequiresContinuity(t *testing.T) {
	grant, _ := AsMap(grantedReply(2)["granted"])
	for name, tc := range map[string]struct {
		status map[string]any
		ok     bool
	}{
		"same generation active": {activeStatus(2), true},
		"generation moved":       {activeStatus(3), false},
		"inactive": {map[string]any{"status": map[string]any{
			"context":  map[string]any{"nativeGeneration": float64(3)},
			"inactive": map[string]any{"reason": "REVOCATION_REASON_DISCONNECT"},
		}}, false},
	} {
		t.Run(name, func(t *testing.T) {
			fw := &fakeWire{replies: map[string][]map[string]any{"authority_read_status": {tc.status}}}
			clock := &ScenarioClock{Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}, Grant: grant}
			err := clock.RenewAuthority(context.Background())
			if (err == nil) != tc.ok {
				t.Fatalf("ok=%v, got err=%v", tc.ok, err)
			}
			if len(fw.sent["authority_control"]) != 0 {
				t.Fatalf("renew must not issue authority_control (it would advance the generation): %#v", fw.sent)
			}
		})
	}
}

func controlReceiptReply(owner string, epochID, startTick, lastTick, tickDeadline int, attempt map[string]any) map[string]any {
	return map[string]any{"receipt": map[string]any{
		"attempt":         attempt,
		"admittedContext": map[string]any{"identity": scenarioIdentity()},
		"applied":         map[string]any{"status": runningStatus(owner, epochID, startTick, lastTick, tickDeadline, 0)},
	}}
}

func TestScenarioClockChangeStartsFreshProfile(t *testing.T) {
	attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}}},
		"clock_start":       {controlReceiptReply("owner-1", 1, 0, 0, 60, attempt)},
	}}
	clock := &ScenarioClock{
		Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{},
		Grant: map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
	}
	status, err := clock.Change(context.Background(), "Superfast", 60)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if status["startTick"] != uint64(0) || status["tickDeadline"] != uint64(60) {
		t.Fatalf("unexpected status: %#v", status)
	}
	if len(clock.Controls) != 1 || clock.Controls[0].Method != "start" {
		t.Fatalf("expected one recorded start control, got %#v", clock.Controls)
	}
}

func TestScenarioClockChangeRejectsUnsupportedSpeed(t *testing.T) {
	clock := &ScenarioClock{Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}}
	if _, err := clock.Change(context.Background(), "Ludicrous", 60); err == nil {
		t.Fatal("expected an error for an unsupported speed")
	}
}

func TestScenarioClockAccelerationRequiresUltrafast(t *testing.T) {
	clock := &ScenarioClock{TestAcceleration: true}
	for _, speed := range []string{"Normal", "Fast", "Superfast"} {
		if _, err := clock.Change(context.Background(), speed, 60); err == nil {
			t.Fatalf("accepted acceleration at %s", speed)
		}
	}
}

func TestScenarioClockChangeRefusesUnderExternalHold(t *testing.T) {
	clock := &ScenarioClock{Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}, Hold: "external_pause"}
	if _, err := clock.Change(context.Background(), "Superfast", 60); err == nil {
		t.Fatal("expected an error under an external clock hold")
	}
}

func TestScenarioClockControlInjectsCombatPolicy(t *testing.T) {
	attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_start": {controlReceiptReply("owner-1", 1, 0, 0, 60, attempt)},
	}}
	clock := &ScenarioClock{Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}, CombatTargets: []string{"raider-1", "raider-2"}}
	request := map[string]any{"authority": map[string]any{"attempt": attempt}, "policy": map[string]any{"mode": "WATCH_MODE_COLONY"}}
	if _, err := clock.Control(context.Background(), "start", request); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	sentPolicy, _ := AsMap(clock.Controls[0].Request["policy"])
	if AsString(sentPolicy["mode"]) != "WATCH_MODE_COMBAT" {
		t.Fatalf("expected combat policy override, got %#v", sentPolicy)
	}
	// The caller's original request map must not be mutated in place.
	if AsString(request["policy"].(map[string]any)["mode"]) != "WATCH_MODE_COLONY" {
		t.Fatalf("caller's request was mutated: %#v", request)
	}
}

func TestScenarioClockCallDetectsHoldReason(t *testing.T) {
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {{"status": stoppedStatus("owner-1", 1, 0, 10, 60, 0, "STOP_REASON_EXTERNAL_PAUSE", true)}},
	}}
	clock := &ScenarioClock{Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}}
	status, err := clock.Call(context.Background(), "status", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if AsString(status["stopReason"]) != "external_pause" {
		t.Fatalf("unexpected status: %#v", status)
	}
	if clock.Hold != "external_pause" {
		t.Fatalf("expected Hold to be set, got %q", clock.Hold)
	}
}

// fakeQuery builds a ScenarioRuntime.Query double keyed by tool name.
type fakeQuery struct {
	byTool map[string][]map[string]any
}

func (f *fakeQuery) query(_ context.Context, _, tool string, _ any) (map[string]any, error) {
	queue := f.byTool[tool]
	if len(queue) == 0 {
		panic("no canned reply for tool " + tool)
	}
	f.byTool[tool] = queue[1:]
	return queue[0], nil
}

func identityToolReply() map[string]any {
	return map[string]any{"colonyId": "colony", "mapId": 0.0, "loadToken": "load"}
}

func TestAdvanceGameCompletesOnTickBudget(t *testing.T) {
	for _, accelerated := range []bool{false, true} {
		t.Run(fmt.Sprintf("accelerated=%t", accelerated), func(t *testing.T) {
			attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
			fw := &fakeWire{replies: map[string][]map[string]any{
				"clock_read_status": {{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}}},
				"clock_start":       {controlReceiptReply("owner-1", 1, 0, 0, 60, attempt)},
				"clock_read_events": {{"page": eventPage(scenarioIdentity(), 0, nil)}},
			}}
			fq := &fakeQuery{byTool: map[string][]map[string]any{
				"home/colony_identity": {identityToolReply(), identityToolReply(), identityToolReply()},
			}}
			clock := &ScenarioClock{
				Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{},
				Grant:            map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
				TestAcceleration: accelerated,
			}
			rt := &ScenarioRuntime{Query: fq.query, Clock: clock, Report: Report{}}

			// The status poll loop asks clock_read_status repeatedly; queue a running
			// reply once then a stopped/tick_budget reply.
			fw.replies["clock_read_status"] = append(fw.replies["clock_read_status"],
				map[string]any{"status": stoppedStatus("owner-1", 1, 0, 60, 60, 0, "STOP_REASON_TICK_BUDGET", true)})

			final, err := AdvanceGame(context.Background(), rt, 60)
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if AsString(final["stopReason"]) != "tick_budget" {
				t.Fatalf("unexpected final status: %#v", final)
			}
			simulation := AsSlice(rt.Report["simulation"])
			if len(simulation) != 1 {
				t.Fatalf("expected one simulation record, got %#v", simulation)
			}
			entry, _ := AsMap(simulation[0])
			if entry["completed"] != true {
				t.Fatalf("expected simulation to record completion: %#v", entry)
			}
			starts := fw.sent["clock_start"]
			if len(starts) != 1 {
				t.Fatalf("expected one start, got %#v", starts)
			}
			wantSpeed := "SPEED_SUPERFAST"
			if accelerated {
				wantSpeed = "SPEED_ULTRAFAST"
			}
			boost, _ := starts[0]["testAcceleration"].(bool)
			if starts[0]["speed"] != wantSpeed || boost != accelerated || starts[0]["maxTicks"] != uint64(60) {
				t.Fatalf("wrong speed, acceleration or tick budget: %#v", starts[0])
			}
		})
	}
}

func TestAdvanceGameRejectsOutOfRangeTicks(t *testing.T) {
	rt := &ScenarioRuntime{Report: Report{}}
	if _, err := AdvanceGame(context.Background(), rt, 0); err == nil {
		t.Fatal("expected an error for zero ticks")
	}
	if _, err := AdvanceGame(context.Background(), rt, 1800001); err == nil {
		t.Fatal("expected an error for a tick budget above the bound")
	}
}

func TestAdvanceGameRejectsMismatchedCombatTargets(t *testing.T) {
	rt := &ScenarioRuntime{Report: Report{}, CombatTargets: []string{"raider-1"}}
	if _, err := AdvanceGame(context.Background(), rt, 60, WithCombatTargets("raider-2")); err == nil {
		t.Fatal("expected an error for combat targets not matching the committed plan")
	}
}

func TestAdvanceGameStopsOnIdentityDrift(t *testing.T) {
	fq := &fakeQuery{byTool: map[string][]map[string]any{
		"home/colony_identity": {
			identityToolReply(),
			{"colonyId": "different-colony", "mapId": 0.0, "loadToken": "load"},
			{"colonyId": "different-colony", "mapId": 0.0, "loadToken": "load"},
		},
	}}
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}}},
		"clock_start":       {controlReceiptReply("owner-1", 1, 0, 0, 60, map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"})},
	}}
	clock := &ScenarioClock{
		Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{},
		Grant: map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
	}
	rt := &ScenarioRuntime{Query: fq.query, Clock: clock, Report: Report{}}
	_, err := AdvanceGame(context.Background(), rt, 60)
	if err == nil {
		t.Fatal("expected an error for a drifted identity")
	}
	var interrupted *ScenarioInterrupted
	if !isScenarioInterrupted(err, &interrupted) || !strings.Contains(interrupted.Reason, "identity") {
		t.Fatalf("expected a ScenarioInterrupted identity-change error, got %v", err)
	}
}

func isScenarioInterrupted(err error, out **ScenarioInterrupted) bool {
	if v, ok := err.(*ScenarioInterrupted); ok {
		*out = v
		return true
	}
	return false
}

func TestLetterApproval(t *testing.T) {
	expected := [][2]string{{"Ancient danger", "ThreatBig"}}
	cases := []struct {
		label, def             string
		strict                 bool
		approved, acknowledged bool
	}{
		{"Ancient danger", "ThreatBig", false, true, false},
		{"Ancient danger", "ThreatBig", true, true, false},
		{"Raid", "ThreatBig", false, false, false},
		{"Cargo pods", "PositiveEvent", false, true, true},
		{"Cargo pods", "PositiveEvent", true, false, false},
		{"Wanderer joins", "AcceptJoiner", false, true, true},
		{"Manhunter pack", "ThreatSmall", false, false, false},
	}
	for _, c := range cases {
		approved, acknowledged := letterApproval(c.label, c.def, expected, c.strict)
		if approved != c.approved || acknowledged != c.acknowledged {
			t.Errorf("%s/%s strict=%v: got approved=%v acknowledged=%v, want %v/%v", c.label, c.def, c.strict, approved, acknowledged, c.approved, c.acknowledged)
		}
	}
}

func TestAdvanceGameCombatContinuesPastColonistHealthStop(t *testing.T) {
	attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
	standing := map[string]any{
		"blocks": map[string]any{"colonists": true, "threats": true}, "skipped": []any{},
		"colonists": []any{map[string]any{"dead": false, "downed": false, "bleeding": true}},
	}
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {
			{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}},
			{"status": stoppedStatus("owner-1", 1, 0, 20, 60, 0, "STOP_REASON_COLONIST_HEALTH", true)},
			// The next Change reads the stopped epoch before starting a fresh one.
			{"status": stoppedStatus("owner-1", 1, 0, 20, 60, 0, "STOP_REASON_COLONIST_HEALTH", true)},
			{"status": stoppedStatus("owner-1", 2, 20, 60, 60, 0, "STOP_REASON_TICK_BUDGET", true)},
		},
		"clock_start": {
			controlReceiptReply("owner-1", 1, 0, 0, 60, attempt),
			controlReceiptReply("owner-1", 2, 20, 20, 60, map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-2", "attemptId": "1"}),
		},
		"clock_read_events": {{"page": eventPage(scenarioIdentity(), 0, nil)}},
	}}
	fq := &fakeQuery{byTool: map[string][]map[string]any{
		"home/colony_identity": {identityToolReply(), identityToolReply(), identityToolReply(), identityToolReply(), identityToolReply()},
		"home/status":          {standing},
	}}
	clock := &ScenarioClock{
		Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}, CombatTargets: []string{"hare-1"},
		Grant: map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
	}
	rt := &ScenarioRuntime{Query: fq.query, Clock: clock, Report: Report{}, CombatTargets: []string{"hare-1"}}
	final, err := AdvanceGame(context.Background(), rt, 60, WithCombatTargets("hare-1"))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if AsString(final["stopReason"]) != "tick_budget" {
		t.Fatalf("unexpected final status: %#v", final)
	}
	entry, _ := AsMap(AsSlice(rt.Report["simulation"])[0])
	interruptions := AsSlice(entry["interruptions"])
	if entry["completed"] != true || len(interruptions) != 1 {
		t.Fatalf("expected one recorded health interruption and completion: %#v", entry)
	}
	if detail, _ := AsMap(interruptions[0]); detail["combatHealth"] != true {
		t.Fatalf("expected the interruption marked as a combat health stop: %#v", detail)
	}
}

func TestAdvanceGameCombatHealthStopInterruptsWhenColonistDowned(t *testing.T) {
	attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
	downed := map[string]any{
		"blocks": map[string]any{"colonists": true, "threats": true}, "skipped": []any{},
		"colonists": []any{map[string]any{"dead": false, "downed": true}},
	}
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {
			{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}},
			{"status": stoppedStatus("owner-1", 1, 0, 20, 60, 0, "STOP_REASON_COLONIST_HEALTH", true)},
			// The interruption's cleanup re-reads the (already stopped) clock.
			{"status": stoppedStatus("owner-1", 1, 0, 20, 60, 0, "STOP_REASON_COLONIST_HEALTH", true)},
		},
		"clock_start": {controlReceiptReply("owner-1", 1, 0, 0, 60, attempt)},
	}}
	fq := &fakeQuery{byTool: map[string][]map[string]any{
		"home/colony_identity": {identityToolReply(), identityToolReply(), identityToolReply(), identityToolReply()},
		"home/status":          {downed},
	}}
	clock := &ScenarioClock{
		Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{}, CombatTargets: []string{"hare-1"},
		Grant: map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
	}
	rt := &ScenarioRuntime{Query: fq.query, Clock: clock, Report: Report{}, CombatTargets: []string{"hare-1"}}
	_, err := AdvanceGame(context.Background(), rt, 60, WithCombatTargets("hare-1"))
	var interrupted *ScenarioInterrupted
	if !isScenarioInterrupted(err, &interrupted) || !strings.Contains(interrupted.Reason, "downed") {
		t.Fatalf("expected a downed-colonist interruption, got %v", err)
	}
}

// A fresh profile (neverStarted) can still hold retained events, as a
// journal restored from a checkpoint bundle does: a stop diagnosis reads
// from just before the start receipt's own started event, never across
// that history (an authority shutdown in it has no canonical owner).
func TestAdvanceGameFreshEpochDiagnosesFromStartReceipt(t *testing.T) {
	attempt := map[string]any{"controllerSessionId": "owner-1", "actionId": "typed-clock-1", "attemptId": "1"}
	receipt := controlReceiptReply("owner-1", 11, 0, 0, 60, attempt)
	receipt["receipt"].(map[string]any)["applied"].(map[string]any)["status"].(map[string]any)["newestCursor"] = float64(11)
	fw := &fakeWire{replies: map[string][]map[string]any{
		"clock_read_status": {
			{"status": map[string]any{"context": scenarioContext(0), "neverStarted": map[string]any{}}},
			{"status": stoppedStatus("owner-1", 11, 0, 20, 60, 12, "STOP_REASON_LETTER_PAUSE", true)},
			// The failure's cleanup re-reads the (already stopped) clock.
			{"status": stoppedStatus("owner-1", 11, 0, 20, 60, 12, "STOP_REASON_LETTER_PAUSE", true)},
		},
		"clock_start": {receipt},
		"clock_read_events": {{"page": eventPage(scenarioIdentity(), 12, []map[string]any{
			{"cursor": float64(11), "context": scenarioContext(0), "owner": map[string]any{"controllerSessionId": "owner-1", "epoch": float64(11)}, "started": map[string]any{}},
			letterPauseEvent(12, 11, "owner-1", "letter-1"),
		})}, {"page": eventPage(scenarioIdentity(), 12, nil)}},
	}}
	fq := &fakeQuery{byTool: map[string][]map[string]any{
		"home/colony_identity": {identityToolReply(), identityToolReply(), identityToolReply(), identityToolReply()},
		"home/status":          {{}},
	}}
	clock := &ScenarioClock{
		Wire: fw.wire, Identity: scenarioIdentity(), Owner: "owner-1", Report: Report{},
		Grant: map[string]any{"context": map[string]any{"nativeGeneration": float64(1)}, "authority": map[string]any{"mode": "MODE_AUTO"}},
	}
	rt := &ScenarioRuntime{Query: fq.query, Clock: clock, Report: Report{}}
	_, err := AdvanceGame(context.Background(), rt, 60)
	// The diagnosis attributes the letter and moves on to the colony.
	if err == nil || !strings.Contains(err.Error(), "Safety observation incomplete") {
		t.Fatalf("expected the diagnosis to reach the letter, got %v", err)
	}
	reads := fw.sent["clock_read_events"]
	if len(reads) != 2 || reads[0]["afterCursor"] != "10" || reads[1]["afterCursor"] != "12" {
		t.Fatalf("expected the event reads to start after cursor 10, got %#v", reads)
	}
}
