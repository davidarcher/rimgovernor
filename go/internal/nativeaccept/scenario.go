package nativeaccept

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// This file is the scenario advance loop and typed scenario clock, so
// the combat and movement acceptance cases can advance real game ticks in a
// bounded, letter-acknowledging way. It intentionally omits a medical-rest wait
// mode: only the combat-target mode is exercised through the typed clock adapter.

// scenarioPolicy is the fixed watch policy every acceptance clock window starts
// with.
var scenarioPolicy = map[string]any{
	"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.1,
	"minHealthFraction": 0.5, "hostileWithin": 40, "injuryStopCooldownMs": 0,
}

// scenarioHoldReasons are the hold reasons:
// stop reasons that mean an external actor now owns the clock's fate.
var scenarioHoldReasons = map[string]bool{
	"external_pause": true, "external_speed_changed": true, "lease_expired": true,
	"session_changed": true, "unavailable": true, "watcher_error": true,
	"start_refused": true, "force_paused": true, "event_journal_error": true,
	"hunting_route_unsafe": true,
}

// ScenarioInterrupted: the
// scenario stopped with retained evidence in the caller's report, not a bug in the
// acceptance code itself. Callers that expect an interruption (e.g. an authority
// revocation test) type-assert for this instead of treating any error the same way.
type ScenarioInterrupted struct{ Reason string }

func (e *ScenarioInterrupted) Error() string { return "scenario interrupted: " + e.Reason }

// ScenarioInteger decodes a ProtoJSON integer (number, uint64 or decimal string)
// as a non-negative integer.
func ScenarioInteger(value any) (uint64, error) { return scenarioInteger(value) }

func scenarioInteger(value any) (uint64, error) {
	switch v := value.(type) {
	case float64:
		if v < 0 {
			return 0, fmt.Errorf("expected a non-negative integer, found %v", v)
		}
		return uint64(v), nil
	case uint64:
		return v, nil
	case int:
		if v < 0 {
			return 0, fmt.Errorf("expected a non-negative integer, found %v", v)
		}
		return uint64(v), nil
	case string:
		n, err := strconv.ParseUint(v, 10, 64)
		if err != nil {
			return 0, fmt.Errorf("expected an integer string, found %q", v)
		}
		return n, nil
	default:
		return 0, fmt.Errorf("expected an integer, found %#v", value)
	}
}

func deepCopyMap(m map[string]any) map[string]any {
	data, err := json.Marshal(m)
	if err != nil {
		return map[string]any{}
	}
	var out map[string]any
	_ = json.Unmarshal(data, &out)
	return out
}

// validateScenarioContext.
func validateScenarioContext(value map[string]any) error {
	identity, ok := AsMap(value["identity"])
	if !ok {
		return fmt.Errorf("context missing identity: %#v", value)
	}
	if AsString(identity["colonyId"]) == "" || AsString(identity["loadToken"]) == "" {
		return fmt.Errorf("context identity missing colonyId/loadToken: %#v", identity)
	}
	mapID, ok := identity["mapId"].(float64)
	if !ok || mapID < 0 {
		return fmt.Errorf("context identity missing mapId: %#v", identity)
	}
	if _, err := scenarioInteger(value["tick"]); err != nil {
		return fmt.Errorf("context tick: %w", err)
	}
	return nil
}

// projectStatus adapts a decoded clock_read_status/clock_pause/receipt status into
// advance_game's protocol.
func projectStatus(status map[string]any) (map[string]any, error) {
	contextValue, ok := AsMap(status["context"])
	if !ok {
		return nil, fmt.Errorf("clock status missing context: %#v", status)
	}
	if err := validateScenarioContext(contextValue); err != nil {
		return nil, err
	}
	var caseName string
	for _, key := range []string{"running", "stopping", "stopped", "neverStarted", "unavailable"} {
		if _, ok := status[key]; ok {
			if caseName != "" {
				return nil, fmt.Errorf("multiple clock status cases: %#v", status)
			}
			caseName = key
		}
	}
	if caseName != "running" && caseName != "stopping" && caseName != "stopped" {
		return nil, fmt.Errorf("unsupported clock status case %q: %#v", caseName, status)
	}
	body, _ := AsMap(status[caseName])
	epoch, ok := AsMap(body["epoch"])
	if !ok {
		return nil, fmt.Errorf("clock status missing epoch: %#v", body)
	}
	origin, ok := AsMap(epoch["origin"])
	if !ok {
		return nil, fmt.Errorf("clock epoch missing origin: %#v", epoch)
	}
	if err := validateScenarioContext(origin); err != nil {
		return nil, err
	}
	owner, ok := AsMap(epoch["owner"])
	if !ok {
		return nil, fmt.Errorf("clock epoch missing owner: %#v", epoch)
	}
	ownerSession := AsString(owner["controllerSessionId"])
	if ownerSession == "" {
		return nil, fmt.Errorf("clock epoch owner missing controllerSessionId: %#v", owner)
	}
	epochID, err := scenarioInteger(owner["epoch"])
	if err != nil || epochID == 0 {
		return nil, fmt.Errorf("clock epoch owner epoch: %#v", owner)
	}
	paused, ok := status["actualPaused"].(bool)
	if !ok {
		return nil, fmt.Errorf("clock status missing actualPaused: %#v", status)
	}
	nativeBoundary, ok := status["nativeTickBoundary"].(bool)
	if !ok {
		return nil, fmt.Errorf("clock status missing nativeTickBoundary: %#v", status)
	}
	startTick, err1 := scenarioInteger(epoch["startTick"])
	lastTick, err2 := scenarioInteger(epoch["lastTick"])
	tickDeadline, err3 := scenarioInteger(epoch["tickDeadline"])
	newestCursor, err4 := scenarioInteger(status["newestCursor"])
	if err := errors.Join(err1, err2, err3, err4); err != nil {
		return nil, fmt.Errorf("clock epoch tick fields: %w", err)
	}
	if !(startTick <= lastTick && lastTick <= tickDeadline) {
		return nil, fmt.Errorf("clock epoch tick ordering violated: start=%d last=%d deadline=%d", startTick, lastTick, tickDeadline)
	}
	result := map[string]any{
		"native": deepCopyMap(status), "owner": ownerSession, "epoch": epochID,
		"active": caseName != "stopped", "startTick": startTick, "lastTick": lastTick,
		"tickDeadline": tickDeadline, "newestCursor": newestCursor, "paused": paused,
		"nativeTickBoundary": nativeBoundary,
	}
	if caseName == "stopped" {
		reason := AsString(body["reason"])
		if !strings.HasPrefix(reason, "STOP_REASON_") || reason == "STOP_REASON_UNSPECIFIED" {
			return nil, fmt.Errorf("invalid stopped reason: %#v", body)
		}
		pauseVerified, ok := body["pauseVerified"].(bool)
		if !ok {
			return nil, fmt.Errorf("stopped clock missing pauseVerified: %#v", body)
		}
		actualPaused, ok := body["actualPaused"].(bool)
		if !ok || actualPaused != paused {
			return nil, fmt.Errorf("stopped clock actualPaused mismatch: %#v", body)
		}
		result["stopReason"] = strings.ToLower(strings.TrimPrefix(reason, "STOP_REASON_"))
		result["pauseVerified"] = pauseVerified
	}
	return result, nil
}

// projectEvents adapts a decoded clock_read_events page.
func projectEvents(page map[string]any, after uint64) (map[string]any, error) {
	contextValue, ok := AsMap(page["context"])
	if !ok {
		return nil, fmt.Errorf("event page missing context: %#v", page)
	}
	if err := validateScenarioContext(contextValue); err != nil {
		return nil, err
	}
	if gap, ok := page["gap"].(bool); !ok || gap {
		return nil, fmt.Errorf("event page reported a gap: %#v", page)
	}
	if lostCount, err := scenarioInteger(page["lostCount"]); err != nil || lostCount != 0 {
		return nil, fmt.Errorf("event page lostCount: %#v", page)
	}
	newest, err := scenarioInteger(page["newestCursor"])
	if err != nil {
		return nil, fmt.Errorf("event page newestCursor: %w", err)
	}
	nextCursor, err := scenarioInteger(page["nextCursor"])
	if err != nil {
		return nil, fmt.Errorf("event page nextCursor: %w", err)
	}
	if after > newest {
		return nil, fmt.Errorf("event page cursor precedes requested afterCursor: %#v", page)
	}
	if oldest, ok := page["oldestCursor"]; ok {
		oldestValue, err := scenarioInteger(oldest)
		if err != nil || oldestValue > newest {
			return nil, fmt.Errorf("event page oldestCursor: %#v", page)
		}
	}
	rawEvents := AsSlice(page["events"])
	rows := make([]any, 0, len(rawEvents))
	previous := after
	for _, raw := range rawEvents {
		event, ok := AsMap(raw)
		if !ok {
			return nil, fmt.Errorf("event row is not an object: %#v", raw)
		}
		cursor, err := scenarioInteger(event["cursor"])
		if err != nil || cursor != previous+1 || cursor > newest {
			return nil, fmt.Errorf("event cursor discontinuity: %#v", page)
		}
		previous = cursor
		eventContext, ok := AsMap(event["context"])
		if !ok {
			return nil, fmt.Errorf("event missing context: %#v", event)
		}
		if err := validateScenarioContext(eventContext); err != nil {
			return nil, err
		}
		owner, ok := AsMap(event["owner"])
		if !ok {
			return nil, fmt.Errorf("event missing owner: %#v", event)
		}
		ownerSession := AsString(owner["controllerSessionId"])
		if ownerSession == "" {
			return nil, fmt.Errorf("event owner missing controllerSessionId: %#v", owner)
		}
		epochID, err := scenarioInteger(owner["epoch"])
		if err != nil || epochID == 0 {
			return nil, fmt.Errorf("event owner epoch: %#v", owner)
		}
		row := map[string]any{"native": deepCopyMap(event), "cursor": cursor, "owner": ownerSession, "epoch": epochID}
		if stopped, ok := AsMap(event["stopped"]); ok {
			reason := AsString(stopped["reason"])
			if !strings.HasPrefix(reason, "STOP_REASON_") || reason == "STOP_REASON_UNSPECIFIED" {
				return nil, fmt.Errorf("invalid event stop reason: %#v", event)
			}
			kind := strings.ToLower(strings.TrimPrefix(reason, "STOP_REASON_"))
			row["kind"] = kind
			if kind == "letter_pause" {
				pause, _ := AsMap(stopped["pause"])
				letter, ok := AsMap(pause["letter"])
				letterID := AsString(letter["id"])
				if !ok || letterID == "" {
					return nil, fmt.Errorf("letter_pause event missing letter id: %#v", event)
				}
				row["event"] = map[string]any{"letterId": letterID, "source": "LetterStack.ReceiveLetter"}
			}
			if kind == "dialog_pause" {
				pause, _ := AsMap(stopped["pause"])
				dialog, ok := AsMap(pause["dialog"])
				if !ok || dialog["windowId"] == nil {
					return nil, fmt.Errorf("dialog_pause event missing window id: %#v", event)
				}
				row["event"] = map[string]any{"windowId": dialog["windowId"], "windowType": AsString(dialog["windowType"]), "title": AsString(dialog["title"])}
			}
		} else {
			var cases []string
			for _, key := range []string{"started", "speedChanged", "notification", "alert", "injuryObserved",
				"observationInvalidated", "hostilesCleared", "pauseFailed", "forcePauseWaiting", "forcePauseCleared"} {
				if _, ok := event[key]; ok {
					cases = append(cases, key)
				}
			}
			if len(cases) != 1 {
				return nil, fmt.Errorf("event carries %d cases, expected exactly one: %#v", len(cases), event)
			}
			row["kind"] = cases[0]
		}
		rows = append(rows, row)
	}
	if nextCursor != previous {
		return nil, fmt.Errorf("event page nextCursor disagrees with the last row's cursor: %#v", page)
	}
	if len(rows) == 0 && nextCursor != newest {
		return nil, fmt.Errorf("empty event page before the newest retained cursor: %#v", page)
	}
	return map[string]any{"native": deepCopyMap(page), "events": rows, "nextCursor": nextCursor, "gap": false}, nil
}

// ScenarioControl records one owned clock control call, so AdvanceGame's caller can
// replay it after a stop/revocation.
type ScenarioControl struct {
	Method  string
	Request map[string]any
	Receipt map[string]any
}

// ScenarioClock is the typed clock/authority transport adapter AdvanceGame drives. Every native
// call goes through Wire (ordinarily Harness.Wire), so this type owns only the
// owner-side bookkeeping (grant, attempt counter, event cursor, replay log)
// AdvanceGame needs to safely resume, replay and detect drift.
type ScenarioClock struct {
	Wire     func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error)
	Identity map[string]any
	Owner    string
	Report   Report

	// CombatTargets, when non-empty, switches every clock_start this clock issues
	// to WATCH_MODE_COMBAT with these acknowledged hostiles.
	CombatTargets []string

	// TestAcceleration opts AdvanceGame into Ultrafast with the native test
	// tick boost. Requires a launch with -rimgovernor-test-acceleration;
	// the zero value preserves Superfast and all native stop boundaries.
	TestAcceleration bool

	// Grant is the current authority grant (SetMode(Auto)'s "granted" body:
	// {context, authority}); its context.nativeGeneration is the generation
	// every owned write below is admitted against. Exported so callers can
	// seed or recover it.
	Grant map[string]any
	// OnStarted, when set, runs after Change() admits a fresh clock_start receipt
	// but before AdvanceGame observes it — e.g. to revoke authority mid-window.
	OnStarted func(ctx context.Context, status map[string]any) error
	// Hold records the last observed external-hold stop reason (HOLD_REASONS);
	// AdvanceGame refuses to proceed once set, mirroring supervisor.hold.
	Hold string
	// Controls is the replay log of every owned control call issued so far.
	Controls []ScenarioControl

	counter int
	cursor  uint64
	epoch   uint64
}

func (s *ScenarioClock) precondition() map[string]any {
	s.counter++
	return map[string]any{
		"identity":           s.Identity,
		"expectedGeneration": dig(s.Grant, "context", "nativeGeneration"),
		"attempt": map[string]any{
			"controllerSessionId": s.Owner,
			"actionId":            "typed-clock-" + strconv.Itoa(s.counter),
			"attemptId":           "1",
		},
	}
}

func dig(m map[string]any, path ...string) any {
	var current any = m
	for _, key := range path {
		next, ok := AsMap(current)
		if !ok {
			return nil
		}
		current = next[key]
	}
	return current
}

// Acquire grants Auto (SetMode(MODE_AUTO)) at the current native generation
// and records the granted body as Grant. The name predates #52's collapse of
// the acquire/renew lease handshake; there is no lease, only the generation.
func (s *ScenarioClock) Acquire(ctx context.Context, label string) (map[string]any, error) {
	grant, err := GrantAuto(ctx, s.Wire, label, s.Identity)
	if err != nil {
		return nil, err
	}
	s.Grant = grant
	return grant, nil
}

// RenewAuthority proves Grant is still the live authority: Active(Auto) at
// exactly Grant's generation. Authority no longer lapses on its own (#52), so
// "renewing" means confirming generation continuity rather than extending a
// lease; it deliberately does not re-issue SetMode, which would advance the
// generation and interrupt every attempt admitted under the current one.
// Callers that need authority back after it moved re-Acquire.
func (s *ScenarioClock) RenewAuthority(ctx context.Context) error {
	if s.Grant == nil {
		return fmt.Errorf("renew requires an existing authority grant")
	}
	status, generation, err := AuthorityStatus(ctx, s.Wire, "authority-renew", s.Identity)
	if err != nil {
		return err
	}
	if AsString(dig(status, "active", "mode")) != "MODE_AUTO" {
		return fmt.Errorf("authority is no longer Auto: %#v", status)
	}
	if generation != GrantGeneration(s.Grant) {
		return fmt.Errorf("authority generation moved from %d to %d", GrantGeneration(s.Grant), generation)
	}
	return nil
}

// Control issues one owned clock control call (start/renew/change_speed) and
// projects its admitted status, mirroring TypedScenarioClock.control().
func (s *ScenarioClock) Control(ctx context.Context, method string, request map[string]any) (map[string]any, error) {
	if method == "start" && len(s.CombatTargets) > 0 {
		request = deepCopyMap(request)
		policy, ok := AsMap(request["policy"])
		if !ok {
			return nil, fmt.Errorf("combat start request missing policy: %#v", request)
		}
		targets := make([]any, len(s.CombatTargets))
		for i, t := range s.CombatTargets {
			targets[i] = t
		}
		policy["mode"] = "WATCH_MODE_COMBAT"
		policy["acknowledgedHostileIds"] = targets
	}
	reply, err := s.Wire(ctx, method, "clock_"+method, request)
	if err != nil {
		return nil, err
	}
	_, receipt, err := Outcome(reply, "receipt")
	if err != nil {
		return nil, err
	}
	if !DeepEqual(receipt["attempt"], dig(request, "authority", "attempt")) {
		return nil, fmt.Errorf("clock receipt attempt mismatch: %#v", receipt)
	}
	if !DeepEqual(dig(receipt, "admittedContext", "identity"), s.Identity) {
		return nil, fmt.Errorf("clock receipt identity mismatch: %#v", receipt)
	}
	var appliedCase map[string]any
	if v, ok := receipt["applied"]; ok {
		appliedCase = map[string]any{"applied": v}
	} else if v, ok := receipt["uncertain"]; ok {
		appliedCase = map[string]any{"uncertain": v}
	} else {
		return nil, fmt.Errorf("clock receipt carries neither applied nor uncertain: %#v", receipt)
	}
	_, applied, err := Outcome(appliedCase, "applied")
	if err != nil {
		return nil, err
	}
	statusValue, ok := AsMap(applied["status"])
	if !ok {
		return nil, fmt.Errorf("applied clock control missing status: %#v", applied)
	}
	projected, err := projectStatus(statusValue)
	if err != nil {
		return nil, err
	}
	if AsString(projected["owner"]) != s.Owner {
		return nil, fmt.Errorf("clock control admitted a different owner: %#v", projected)
	}
	if !DeepEqual(dig(statusValue, "context", "identity"), s.Identity) {
		return nil, fmt.Errorf("clock control status identity mismatch: %#v", statusValue)
	}
	s.Controls = append(s.Controls, ScenarioControl{Method: method, Request: deepCopyMap(request), Receipt: deepCopyMap(receipt)})
	if s.Report != nil {
		s.Report["controls"] = s.Controls
	}
	s.epoch, _ = scenarioInteger(projected["epoch"])
	return projected, nil
}

// Change starts a bounded native clock window. TestAcceleration requires
// Ultrafast; native admission enforces the test-only launch gate.
func (s *ScenarioClock) Change(ctx context.Context, speed string, maxTicks uint64) (map[string]any, error) {
	if speed != "Normal" && speed != "Fast" && speed != "Superfast" && speed != "Ultrafast" {
		return nil, fmt.Errorf("unsupported clock speed %q", speed)
	}
	if s.TestAcceleration && speed != "Ultrafast" {
		return nil, fmt.Errorf("test acceleration requires Ultrafast")
	}
	if maxTicks == 0 {
		return nil, fmt.Errorf("maxTicks must be positive")
	}
	if s.Hold != "" {
		return nil, fmt.Errorf("external clock hold: %s", s.Hold)
	}
	before, err := s.Wire(ctx, "clock-before-start", "clock_read_status", map[string]any{"identity": s.Identity})
	if err != nil {
		return nil, err
	}
	_, beforeStatus, err := Outcome(before, "status")
	if err != nil {
		return nil, err
	}
	if !DeepEqual(dig(beforeStatus, "context", "identity"), s.Identity) {
		return nil, fmt.Errorf("pre-start status identity mismatch: %#v", beforeStatus)
	}
	var watermark uint64
	var freshEpoch bool
	if cursor, ok := beforeStatus["newestCursor"]; ok {
		watermark, err = scenarioInteger(cursor)
		if err != nil {
			return nil, err
		}
	} else {
		if _, fresh := beforeStatus["neverStarted"]; !fresh || len(s.Controls) != 0 {
			return nil, fmt.Errorf("expected a fresh profile with no owned epoch: %#v", beforeStatus)
		}
		freshEpoch = true
	}
	request := map[string]any{
		"authority": s.precondition(),
		"speed":     "SPEED_" + strings.ToUpper(speed),
		"policy":    deepCopyMap(scenarioPolicy),
		"leaseMs":   30000,
		"maxTicks":  maxTicks,
	}
	if s.TestAcceleration {
		request["testAcceleration"] = true
	}
	status, err := s.Control(ctx, "start", request)
	if err != nil {
		return nil, err
	}
	boundary, _ := status["nativeTickBoundary"].(bool)
	tickDeadline, _ := status["tickDeadline"].(uint64)
	startTick, _ := status["startTick"].(uint64)
	if !boundary || tickDeadline-startTick != maxTicks {
		return nil, fmt.Errorf("clock start did not admit the requested tick budget: %#v", status)
	}
	if s.OnStarted != nil {
		if err := s.OnStarted(ctx, status); err != nil {
			return nil, err
		}
	}
	if n, _ := status["newestCursor"].(uint64); freshEpoch && n > 0 {
		// A fresh profile can still hold retained events: a journal restored
		// from a checkpoint bundle replays the epochs of earlier runs, and an
		// authority shutdown among them has no canonical owner. The receipt's
		// newestCursor is this window's own started event, so a stop
		// diagnosis (letter_pause) reads from just before it, never across
		// that history.
		watermark = n - 1
	}
	status["newestCursor"] = watermark
	// freshEpoch marks a window whose pre-start status reported neverStarted
	// (durableEvents=false is a legitimate, native-acknowledged state in that
	// case, mirroring go/internal/policy/clock_window.go's own admission check).
	// AdvanceGame must not poll clock_read_events for such a window: with no
	// prior owned epoch, cursor 0 can still land on retained events from
	// unrelated activity (e.g. authority acquire/revoke churn) that the native
	// clock reader refuses as lacking canonical ownership, rather than an empty
	// page.
	status["freshEpoch"] = freshEpoch
	return status, nil
}

// Call issues a read-only clock op ("status" or "events") or an owned "pause",
// mirroring TypedScenarioClock.call().
func (s *ScenarioClock) Call(ctx context.Context, op string, arguments map[string]any) (map[string]any, error) {
	switch op {
	case "events":
		after, err := scenarioInteger(arguments["afterCursor"])
		if err != nil {
			return nil, err
		}
		reply, err := s.Wire(ctx, "clock-events", "clock_read_events", map[string]any{
			"identity": s.Identity, "afterCursor": strconv.FormatUint(after, 10), "limit": arguments["limit"],
		})
		if err != nil {
			return nil, err
		}
		_, page, err := Outcome(reply, "page")
		if err != nil {
			return nil, err
		}
		return projectEvents(page, after)
	case "status":
		reply, err := s.Wire(ctx, "clock-status", "clock_read_status", map[string]any{"identity": s.Identity})
		if err != nil {
			return nil, err
		}
		_, status, err := Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		return s.finishStatus(status)
	case "pause":
		owner := AsString(arguments["owner"])
		if owner != s.Owner {
			return nil, fmt.Errorf("pause owner mismatch: %v", arguments["owner"])
		}
		reply, err := s.Wire(ctx, "clock-pause", "clock_pause", map[string]any{
			"identity": s.Identity,
			"owner":    map[string]any{"controllerSessionId": owner, "epoch": fmt.Sprint(arguments["epoch"])},
		})
		if err != nil {
			return nil, err
		}
		_, status, err := Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		return s.finishStatus(status)
	default:
		return nil, fmt.Errorf("unsupported scenario clock op %q", op)
	}
}

func (s *ScenarioClock) finishStatus(status map[string]any) (map[string]any, error) {
	if !DeepEqual(dig(status, "context", "identity"), s.Identity) {
		return nil, fmt.Errorf("clock status identity mismatch: %#v", status)
	}
	projected, err := projectStatus(status)
	if err != nil {
		return nil, err
	}
	if reason := AsString(projected["stopReason"]); scenarioHoldReasons[reason] {
		s.Hold = reason
	}
	return projected, nil
}

// SeekEvents moves the Poll cursor forward to Change()'s newestCursor (the
// exclusive pre-dispatch watermark) so a case that polls the events of a
// window it started itself reads from that window, never from the retained
// authority churn before it (see AdvanceGame). Never regresses.
func (s *ScenarioClock) SeekEvents(started map[string]any) {
	if cursor, _ := started["newestCursor"].(uint64); cursor > s.cursor {
		s.cursor = cursor
	}
}

// Poll drains events since the last Poll, mirroring TypedScenarioClock.poll().
func (s *ScenarioClock) Poll(ctx context.Context) ([]any, error) {
	batch, err := s.Call(ctx, "events", map[string]any{"afterCursor": s.cursor, "limit": 128})
	if err != nil {
		return nil, err
	}
	s.cursor, _ = scenarioInteger(batch["nextCursor"])
	return AsSlice(batch["events"]), nil
}

// ScenarioRuntime is the minimal advance_game(rt, ...) surface a Go acceptance
// binary needs.
// It has no review-task/decision-loop concurrency: every acceptance binary drives
// its scenario from one goroutine.
type ScenarioRuntime struct {
	// Query issues a generic native/MCP tool call (e.g. Harness.Call) for the
	// game-observation reads AdvanceGame needs (home/colony_identity, home/status)
	// outside the rimgovernor/* protobuf surface ScenarioClock.Wire covers.
	Query  func(ctx context.Context, label, tool string, arguments any) (map[string]any, error)
	Clock  *ScenarioClock
	Report Report

	// CombatTargets mirrors rt.current_plan.control["combat"]["targets"]: the
	// committed defense targets AdvanceGame's combat-target option must match.
	// Leave nil outside combat scenarios.
	CombatTargets []string

	// Tools are the discovered tool names; when they include DismissLetterTool
	// AdvanceGame takes each acknowledged letter off the stack. Nil leaves
	// letters on the stack, as before the fixture existed.
	Tools []string

	clockEvents []any
}

// DismissLetterTool removes one letter from the stack
// (scripts/fixtures/LetterFixture.cs); every fixture build carries it.
const DismissLetterTool = "test/dismiss_letter"

// AcknowledgedLetterDefs are the letter defs AdvanceGame acknowledges by
// default (issue #92): informational events and the choice letters whose
// unanswered outcome is harmless to an assertion (a joiner leaves, a quest
// stays offered). Threat letters are never here: they still need
// WithExpectedLetters, and the window's own safety checks (no hostiles, no
// downed or bleeding colonist) run for every letter regardless.
var AcknowledgedLetterDefs = map[string]bool{
	"NeutralEvent": true, "PositiveEvent": true, "NegativeEvent": true,
	"NewQuest": true, "AcceptJoiner": true, "AcceptVisitors": true, "AcceptCreepJoiner": true,
	"RitualOutcomePositive": true, "RitualOutcomeNegative": true,
	"BabyBirth": true, "BabyToChild": true, "ChildToAdult": true, "ChildBirthday": true,
}

// letterApproval classifies one letter for an advance window: expected
// letters (label and def listed) are inspected fixture warnings and stay on
// the stack; acknowledged defs are informational and are dismissed; anything
// else interrupts. strict (WithExpectedLetters) disables acknowledgement.
func letterApproval(label, def string, expected [][2]string, strict bool) (approved, acknowledged bool) {
	for _, pair := range expected {
		if pair[0] == label && pair[1] == def {
			return true, false
		}
	}
	if !strict && AcknowledgedLetterDefs[def] {
		return true, true
	}
	return false, false
}

func (rt *ScenarioRuntime) receiveClockEvents() {
	delivered := AsSlice(rt.Report["delivered_events"])
	delivered = append(delivered, rt.clockEvents...)
	rt.Report["delivered_events"] = delivered
	rt.clockEvents = nil
}

func (rt *ScenarioRuntime) note(kind, message string, details map[string]any) {
	notes := AsSlice(rt.Report["notes"])
	entry := map[string]any{"kind": kind, "message": message}
	for k, v := range details {
		entry[k] = v
	}
	rt.Report["notes"] = append(notes, entry)
}

func (rt *ScenarioRuntime) identityNow(ctx context.Context) (map[string]any, error) {
	value, err := rt.Query(ctx, "scenario-identity", "home/colony_identity", nil)
	if err != nil {
		return nil, err
	}
	out := map[string]any{}
	for _, key := range []string{"colonyId", "mapId", "loadToken"} {
		v, ok := value[key]
		if !ok || v == nil {
			return nil, &ScenarioInterrupted{"Native identity unavailable"}
		}
		out[key] = v
	}
	return out, nil
}

// combatHealthStopsPerAdvance bounds the colonist_health stops one combat
// AdvanceGame call continues past; the native guard needs a fresh drop of
// healthDropFraction per window, so this many is a colonist near downing.
const combatHealthStopsPerAdvance = 8

// AdvanceOption configures one AdvanceGame call.
type AdvanceOption func(*advanceOptions)

type advanceOptions struct {
	timeout         time.Duration
	expectedLetters [][2]string
	strictLetters   bool
	combatTargets   []string
}

// WithTimeout bounds the whole advance, like advance_game's timeout= keyword.
func WithTimeout(d time.Duration) AdvanceOption { return func(o *advanceOptions) { o.timeout = d } }

// WithExpectedLetters makes the window strict: only the listed (label, def)
// pairs may pause it and AcknowledgedLetterDefs no longer apply. Pass no
// pairs (WithExpectedLetters()) for interruption acceptance, matching
// advance_game's expected_letters=().
func WithExpectedLetters(letters ...[2]string) AdvanceOption {
	return func(o *advanceOptions) { o.expectedLetters, o.strictLetters = letters, true }
}

// WithCombatTargets requires the exact committed defense targets already on
// rt.CombatTargets, mirroring advance_game's combat_targets= keyword.
func WithCombatTargets(targets ...string) AdvanceOption {
	return func(o *advanceOptions) { o.combatTargets = targets }
}

// AdvanceGame advances exactly ticks native ticks, acknowledging inspected
// fixture warnings and, unless the window is strict, the informational
// letters in AcknowledgedLetterDefs (dismissed through DismissLetterTool when
// rt.Tools carries it, recorded under the interruption either way). A combat
// window (WithCombatTargets) also continues past a colonist_health stop while
// every colonist still stands. It never clears player holds or retries game
// orders; an unexpected injury, new threat, or drifted identity raises
// *ScenarioInterrupted with the failing evidence retained on rt.Report.
func AdvanceGame(ctx context.Context, rt *ScenarioRuntime, ticks uint64, opts ...AdvanceOption) (map[string]any, error) {
	if ticks < 1 || ticks > 1800000 {
		return nil, fmt.Errorf("ticks must be 1..1800000, got %d", ticks)
	}
	options := advanceOptions{timeout: 120 * time.Second, expectedLetters: [][2]string{{"Ancient danger", "ThreatBig"}}}
	for _, opt := range opts {
		opt(&options)
	}
	if len(options.combatTargets) > 0 {
		seen := map[string]bool{}
		for _, t := range options.combatTargets {
			if t == "" || seen[t] {
				return nil, fmt.Errorf("combat waits require distinct non-empty targets")
			}
			seen[t] = true
		}
		committed := map[string]bool{}
		for _, t := range rt.CombatTargets {
			committed[t] = true
		}
		if len(committed) != len(seen) {
			return nil, fmt.Errorf("combat waits require the exact committed defense targets")
		}
		for t := range seen {
			if !committed[t] {
				return nil, fmt.Errorf("combat waits require the exact committed defense targets")
			}
		}
	}

	evidence := map[string]any{"ticks": ticks, "windows": []any{}, "interruptions": []any{}}
	simulation := AsSlice(rt.Report["simulation"])
	simulation = append(simulation, evidence)
	rt.Report["simulation"] = simulation

	require := func(condition bool, reason string) error {
		if condition {
			return nil
		}
		evidence["failure"] = reason
		return &ScenarioInterrupted{reason}
	}

	ctx, cancel := context.WithTimeout(ctx, options.timeout)
	defer cancel()

	supervisor := rt.Clock
	identity, err := rt.identityNow(ctx)
	if err != nil {
		return nil, err
	}

	var state map[string]any
	var previousTick uint64
	havePreviousTick := false
	remaining := ticks
	seen := map[string]bool{}
	healthStops := 0

	runErr := func() error {
		for remaining > 0 {
			now, err := rt.identityNow(ctx)
			if err != nil {
				return err
			}
			if err := require(DeepEqual(now, identity), "Native identity changed"); err != nil {
				return err
			}
			speed := "Superfast"
			if supervisor.TestAcceleration {
				speed = "Ultrafast"
			}
			started, err := supervisor.Change(ctx, speed, remaining)
			if err != nil {
				return err
			}
			startTick, _ := started["startTick"].(uint64)
			if err := require(!havePreviousTick || startTick == previousTick, "Native ticks changed between windows"); err != nil {
				return err
			}
			cursor, _ := started["newestCursor"].(uint64)
			// Poll (only reachable below via the tick_budget completion path) reads
			// from supervisor.cursor, which starts at zero; without this, a
			// supervisor whose owned epoch began after other activity already wrote
			// retained events (e.g. authority acquire/revoke churn before the clock
			// ever started) would poll from before those events and be refused with
			// FAILURE_CODE_UNAVAILABLE ("retained legacy event lacks canonical
			// ownership"). watermark/newestCursor is exactly the exclusive
			// pre-dispatch cursor Start already computed to exclude that noise, so
			// seed supervisor.cursor with it (monotonically, never regressing).
			if cursor > supervisor.cursor {
				supervisor.cursor = cursor
			}
			for {
				state, err = supervisor.Call(ctx, "status", nil)
				if err != nil {
					return err
				}
				active, _ := state["active"].(bool)
				if !active {
					break
				}
				select {
				case <-ctx.Done():
					return ctx.Err()
				case <-time.After(100 * time.Millisecond):
				}
			}
			evidence["windows"] = append(AsSlice(evidence["windows"]), state)
			now, err = rt.identityNow(ctx)
			if err != nil {
				return err
			}
			if err := require(DeepEqual(now, identity), "Native identity changed"); err != nil {
				return err
			}
			startedEpoch, _ := started["epoch"].(uint64)
			stateEpoch, _ := state["epoch"].(uint64)
			if err := require(AsString(state["owner"]) == supervisor.Owner && stateEpoch == startedEpoch, "Clock ownership changed"); err != nil {
				return err
			}
			if err := require(state["pauseVerified"] == true, "Native pause unverified"); err != nil {
				return err
			}
			lastTick, _ := state["lastTick"].(uint64)
			advanced := lastTick - startTick
			if err := require(advanced <= remaining, "Invalid native tick progress"); err != nil {
				return err
			}
			remaining -= advanced
			previousTick, havePreviousTick = lastTick, true
			stopReason := AsString(state["stopReason"])
			if stopReason == "tick_budget" {
				if err := require(remaining == 0, "Native tick budget ended early"); err != nil {
					return err
				}
				if fresh, _ := started["freshEpoch"].(bool); !fresh {
					events, err := supervisor.Poll(ctx)
					if err != nil {
						return err
					}
					rt.clockEvents = append(rt.clockEvents, events...)
					rt.receiveClockEvents()
				}
				if err := require(supervisor.Hold == "", "External clock hold"); err != nil {
					return err
				}
				evidence["completed"] = true
				return nil
			}

			detail := map[string]any{"clock": state, "events": []any{}}
			evidence["interruptions"] = append(AsSlice(evidence["interruptions"]), detail)
			if stopReason == "colonist_health" && len(options.combatTargets) > 0 {
				// A colonist ordered to engage the committed targets may take
				// the wounds the combat policy flags; the stop is recorded and
				// the next window continues so the fight can resolve (issue
				// #182). Each window baselines health afresh, so a repeat needs
				// a further drop; the cap keeps a pawn who bleeds out under
				// the guard from consuming the whole tick budget window by
				// window. A downed or dead colonist still interrupts.
				healthStops++
				detail["combatHealth"] = true
				if err := require(healthStops <= combatHealthStopsPerAdvance, "Combat health stops exhausted"); err != nil {
					return err
				}
				status, err := rt.Query(ctx, "scenario-status", "home/status", map[string]any{"colonists": true, "threats": true})
				if err != nil {
					return err
				}
				detail["status"] = status
				blocks, _ := AsMap(status["blocks"])
				colonistsBlocked, _ := blocks["colonists"].(bool)
				if err := require(len(AsSlice(status["skipped"])) == 0 && colonistsBlocked, "Safety observation incomplete"); err != nil {
					return err
				}
				standing := len(AsSlice(status["colonists"])) > 0
				for _, raw := range AsSlice(status["colonists"]) {
					pawn, ok := AsMap(raw)
					dead, _ := pawn["dead"].(bool)
					downed, _ := pawn["downed"].(bool)
					if !ok || dead || downed {
						standing = false
					}
				}
				if err := require(standing, "Colonist downed under the combat health guard"); err != nil {
					return err
				}
				continue
			}
			if err := require(stopReason == "letter_pause", "Unexpected native interruption"); err != nil {
				return err
			}
			for {
				batch, err := supervisor.Call(ctx, "events", map[string]any{"afterCursor": cursor, "limit": 128})
				if err != nil {
					return err
				}
				detail["events"] = append(AsSlice(detail["events"]), AsSlice(batch["events"])...)
				gap, _ := batch["gap"].(bool)
				if err := require(!gap, "Native event history incomplete"); err != nil {
					return err
				}
				nextCursor, _ := batch["nextCursor"].(uint64)
				if nextCursor == cursor {
					break
				}
				cursor = nextCursor
			}
			var stops []map[string]any
			for _, raw := range AsSlice(detail["events"]) {
				row, ok := AsMap(raw)
				if !ok {
					continue
				}
				rowEpoch, _ := row["epoch"].(uint64)
				if rowEpoch == stateEpoch && AsString(row["kind"]) == "letter_pause" {
					stops = append(stops, row)
				}
			}
			if err := require(len(stops) == 1, "Triggering letter unavailable"); err != nil {
				return err
			}
			trigger, _ := AsMap(stops[0]["event"])
			letterID := AsString(trigger["letterId"])
			if err := require(AsString(trigger["source"]) == "LetterStack.ReceiveLetter" && letterID != "" && !seen[letterID],
				"Letter attribution unavailable or repeated"); err != nil {
				return err
			}
			status, err := rt.Query(ctx, "scenario-status", "home/status", map[string]any{"colonists": true, "threats": true})
			if err != nil {
				return err
			}
			detail["status"] = status
			blocks, _ := AsMap(status["blocks"])
			colonistsBlocked, _ := blocks["colonists"].(bool)
			threatsBlocked, _ := blocks["threats"].(bool)
			if err := require(len(AsSlice(status["skipped"])) == 0 && colonistsBlocked && threatsBlocked, "Safety observation incomplete"); err != nil {
				return err
			}
			var matchedLetters []map[string]any
			for _, raw := range AsSlice(status["letters"]) {
				row, ok := AsMap(raw)
				if ok && AsString(row["id"]) == letterID {
					matchedLetters = append(matchedLetters, row)
				}
			}
			approved, acknowledged := false, false
			if len(matchedLetters) == 1 {
				detail["letter"] = matchedLetters[0]
				approved, acknowledged = letterApproval(AsString(matchedLetters[0]["label"]), AsString(matchedLetters[0]["letterDef"]), options.expectedLetters, options.strictLetters)
			}
			if err := require(approved, "Letter is not approved for this scenario"); err != nil {
				return err
			}
			ui, _ := AsMap(status["ui"])
			timeStatus, _ := AsMap(status["time"])
			modalOpen, _ := ui["modalOpen"].(bool)
			timePaused, _ := timeStatus["paused"].(bool)
			if err := require(!modalOpen && timePaused, "Modal or unverified pause"); err != nil {
				return err
			}
			counts, _ := AsMap(status["counts"])
			hostileCount, hunting := AsNumber(counts["hostileCount"]), AsNumber(counts["huntingPredatorCount"])
			if err := require(hostileCount == 0 && hunting == 0, "Active threat"); err != nil {
				return err
			}
			pawns := AsSlice(status["colonists"])
			allSafe := len(pawns) > 0
			downedCount := 0
			for _, raw := range pawns {
				pawn, ok := AsMap(raw)
				if !ok {
					allSafe = false
					break
				}
				dead, _ := pawn["dead"].(bool)
				bleeding, _ := pawn["bleeding"].(bool)
				downed, _ := pawn["downed"].(bool)
				if dead || bleeding {
					allSafe = false
				}
				if downed {
					downedCount++
					allSafe = false
				}
			}
			if err := require(allSafe && int(AsNumber(counts["downedCount"])) == downedCount && downedCount == 0, "Colonist safety unverified"); err != nil {
				return err
			}
			now, err = rt.identityNow(ctx)
			if err != nil {
				return err
			}
			if err := require(DeepEqual(now, identity), "Native identity changed"); err != nil {
				return err
			}
			fresh, err := supervisor.Call(ctx, "status", nil)
			if err != nil {
				return err
			}
			freshActive, _ := fresh["active"].(bool)
			sameFacts := AsString(fresh["owner"]) == AsString(state["owner"]) &&
				fresh["epoch"] == state["epoch"] && fresh["lastTick"] == state["lastTick"] &&
				AsString(fresh["stopReason"]) == AsString(state["stopReason"]) && fresh["newestCursor"] == state["newestCursor"]
			if err := require(sameFacts && !freshActive, "Clock changed during inspection"); err != nil {
				return err
			}
			seen[letterID] = true
			detail["acknowledgedLetterId"] = letterID
			if acknowledged {
				detail["informational"] = true
				if Contains(rt.Tools, DismissLetterTool) {
					dismissed, err := rt.Query(ctx, "dismiss-letter", DismissLetterTool, map[string]any{"letterId": letterID})
					if err != nil {
						return fmt.Errorf("%s: %w", DismissLetterTool, err)
					}
					detail["dismissed"] = dismissed
				}
				rt.note("scenario_interruption", "Acknowledged informational letter", detail)
			} else {
				rt.note("scenario_interruption", "Acknowledged inspected fixture warning", detail)
			}
			events, err := supervisor.Poll(ctx)
			if err != nil {
				return err
			}
			rt.clockEvents = append(rt.clockEvents, events...)
			rt.receiveClockEvents()
			if err := require(supervisor.Hold == "", "External clock hold"); err != nil {
				return err
			}
		}
		evidence["completed"] = true
		return nil
	}()

	if runErr != nil {
		if _, ok := evidence["failure"]; !ok {
			evidence["failure"] = runErr.Error()
		}
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer cleanupCancel()
		if now, err := rt.identityNow(cleanupCtx); err == nil && DeepEqual(now, identity) {
			if current, err := supervisor.Call(cleanupCtx, "status", nil); err == nil {
				active, _ := current["active"].(bool)
				if active && AsString(current["owner"]) == supervisor.Owner {
					if paused, err := supervisor.Call(cleanupCtx, "pause", map[string]any{"owner": supervisor.Owner, "epoch": current["epoch"]}); err == nil {
						evidence["cleanup"] = paused
					} else {
						evidence["cleanupFailure"] = err.Error()
					}
				}
			}
		}
		return nil, runErr
	}
	return state, nil
}
