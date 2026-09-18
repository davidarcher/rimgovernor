// Command letteraccept proves AdvanceGame's letter handling (issue #92):
// an informational letter that pauses the clock is acknowledged, dismissed
// through test/dismiss_letter and the window completes; an informational
// letter that does not pause under the profile's AutomaticPauseMode leaves
// the window untouched; an unexpected threat letter still interrupts, and a
// strict window (WithExpectedLetters()) interrupts on an informational one.
// It also checks rimgovernor/presentation_notifications (#79) against the
// home/status letter stack while a letter is up.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-letter-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 15*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-letter-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("AdvanceGame acknowledges and dismisses informational letters, ignores ones that do not pause, "+
		"and still interrupts on an unexpected threat letter or, in strict mode, on any letter.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	s, err := na.OpenSession(ctx, cfg, report, na.DebugStart{}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer s.Close()
	h, identity, names := s.Harness, s.Identity, s.Names
	for _, tool := range []string{na.DismissLetterTool, "test/deliver_letter", "test/letter_pause_mode", "rimgovernor/presentation_notifications"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("%s is not installed: build the mod with -Fixture LetterFixture", tool)
		}
	}
	supervisor := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: na.Controller, Report: report}
	if _, err := supervisor.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names}

	deliver := func(label, definition string) (string, error) {
		reply, err := h.Call(ctx, label, "test/deliver_letter", map[string]any{"definition": definition, "label": label, "delayTicks": 60})
		if err != nil {
			return "", err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return "", fmt.Errorf("%s: deliver_letter refused: %#v", label, reply)
		}
		return na.AsString(reply["letterId"]), nil
	}
	onStack := func(label, letterID string) (bool, error) {
		status, err := h.Call(ctx, label, "home/status", map[string]any{})
		if err != nil {
			return false, err
		}
		for _, raw := range na.AsSlice(status["letters"]) {
			if row, ok := na.AsMap(raw); ok && na.AsString(row["id"]) == letterID {
				return true, nil
			}
		}
		return false, nil
	}
	notesSince := func(from int) []map[string]any {
		var out []map[string]any
		notes := na.AsSlice(report["notes"])
		for i := from; i < len(notes); i++ {
			if row, ok := na.AsMap(notes[i]); ok && na.AsString(row["kind"]) == "scenario_interruption" {
				out = append(out, row)
			}
		}
		return out
	}
	setPauseMode := func(mode string) error {
		reply, err := h.Call(ctx, "pause-mode-"+mode, "test/letter_pause_mode", map[string]any{"mode": mode})
		if err != nil {
			return err
		}
		if na.AsString(reply["mode"]) != mode {
			return fmt.Errorf("letter_pause_mode did not take: %#v", reply)
		}
		return nil
	}
	// The strict-mode and unexpected-threat windows leave the letter on the
	// stack and the clock paused; clear the letter before the next window.
	dismiss := func(label, letterID string) error {
		reply, err := h.Call(ctx, label, na.DismissLetterTool, map[string]any{"letterId": letterID})
		if err != nil {
			return err
		}
		if removed, _ := na.AsBool(reply["removed"]); !removed {
			return fmt.Errorf("%s: letter %s was not on the stack: %#v", label, letterID, reply)
		}
		return nil
	}
	// Prefs are process-wide and outlive a reused game (RIMGOVERNOR_ACCEPT_KEEP_GAME):
	// put the pause mode back for the next harness.
	initial, err := h.Call(ctx, "pause-mode-initial", "test/letter_pause_mode", map[string]any{})
	if err != nil {
		return err
	}
	defer func() { _ = setPauseMode(na.AsString(initial["mode"])) }()
	summary := map[string]any{}
	report["letters"] = summary

	// 1. Under the headless profile's pause mode (MajorThreat, set here
	// explicitly so a reused process cannot hand over another) an
	// informational letter does not pause: no interruption, and the letter
	// stays on the stack for the harness to clear.
	if err := setPauseMode("MajorThreat"); err != nil {
		return err
	}
	before := len(na.AsSlice(report["notes"]))
	quietID, err := deliver("quiet-positive", "PositiveEvent")
	if err != nil {
		return err
	}
	if _, err := na.AdvanceGame(ctx, rt, 300, na.WithTimeout(120*time.Second)); err != nil {
		return fmt.Errorf("non-pausing informational window: %w", err)
	}
	if n := len(notesSince(before)); n != 0 {
		return fmt.Errorf("non-pausing informational letter interrupted the window %d times", n)
	}
	stacked, err := onStack("quiet-stack", quietID)
	if err != nil {
		return err
	}
	if !stacked {
		return fmt.Errorf("non-pausing informational letter %s never reached the stack", quietID)
	}
	if err := dismiss("quiet-dismiss", quietID); err != nil {
		return err
	}
	summary["non_pausing"] = map[string]any{"letterId": quietID, "interruptions": 0}

	// 2. Pausing on any letter: the informational letter pauses the clock,
	// AdvanceGame acknowledges and dismisses it and the window completes.
	if err := setPauseMode("AnyLetter"); err != nil {
		return err
	}
	before = len(na.AsSlice(report["notes"]))
	ackID, err := deliver("acknowledged-positive", "PositiveEvent")
	if err != nil {
		return err
	}
	final, err := na.AdvanceGame(ctx, rt, 300, na.WithTimeout(120*time.Second))
	if err != nil {
		return fmt.Errorf("acknowledged informational window: %w", err)
	}
	acks := notesSince(before)
	if len(acks) != 1 || na.AsString(acks[0]["acknowledgedLetterId"]) != ackID || acks[0]["informational"] != true {
		return fmt.Errorf("expected one informational acknowledgement of %s, got %#v", ackID, acks)
	}
	dismissed, _ := na.AsMap(acks[0]["dismissed"])
	if removed, _ := na.AsBool(dismissed["removed"]); !removed {
		return fmt.Errorf("acknowledged letter was not dismissed: %#v", acks[0])
	}
	if stacked, err := onStack("acknowledged-stack", ackID); err != nil {
		return err
	} else if stacked {
		return fmt.Errorf("acknowledged letter %s is still on the stack", ackID)
	}
	if na.AsString(final["stopReason"]) != "tick_budget" {
		return fmt.Errorf("acknowledged window did not run to its budget: %#v", final)
	}
	summary["acknowledged"] = map[string]any{"letterId": ackID, "dismissed": dismissed}

	// 3. An unexpected threat letter still interrupts, even with acknowledgement on.
	threatID, err := deliver("unexpected-threat", "ThreatBig")
	if err != nil {
		return err
	}
	var interrupted *na.ScenarioInterrupted
	_, err = na.AdvanceGame(ctx, rt, 300, na.WithTimeout(120*time.Second))
	if !errors.As(err, &interrupted) || interrupted.Reason != "Letter is not approved for this scenario" {
		return fmt.Errorf("unexpected threat letter should interrupt the window, got %v", err)
	}
	if err := dismiss("threat-dismiss", threatID); err != nil {
		return err
	}
	summary["unexpected_threat"] = map[string]any{"letterId": threatID, "reason": interrupted.Reason}

	// 4. A strict window (interruption harnesses) refuses the informational letter too.
	strictID, err := deliver("strict-positive", "PositiveEvent")
	if err != nil {
		return err
	}
	_, err = na.AdvanceGame(ctx, rt, 300, na.WithTimeout(120*time.Second), na.WithExpectedLetters())
	if !errors.As(err, &interrupted) || interrupted.Reason != "Letter is not approved for this scenario" {
		return fmt.Errorf("strict window should interrupt on an informational letter, got %v", err)
	}
	if stacked, err := onStack("strict-stack", strictID); err != nil {
		return err
	} else if !stacked {
		return fmt.Errorf("strict window dismissed letter %s", strictID)
	}
	// 5. The typed notifications read (#79) sees the same stack as home/status
	// and answers every section; an explicit include_letters:false omits only
	// the letter section.
	typed, err := notificationsMatch(ctx, h, identity, strictID)
	if err != nil {
		return err
	}
	summary["typed_notifications"] = typed
	if err := dismiss("strict-dismiss", strictID); err != nil {
		return err
	}
	summary["strict"] = map[string]any{"letterId": strictID, "reason": interrupted.Reason}
	return nil
}

// notificationsMatch reads rimgovernor/presentation_notifications and checks
// it against home/status: the letter under test is listed with its def, the
// letter listing counts agree, messages and alerts are observed, and an
// explicit include_letters:false leaves the letter section absent.
func notificationsMatch(ctx context.Context, h *na.Harness, identity map[string]any, letterID string) (map[string]any, error) {
	status, err := h.Call(ctx, "notifications-status", "home/status", map[string]any{})
	if err != nil {
		return nil, err
	}
	statusLetters := na.AsSlice(status["letters"])
	reply, err := h.Wire(ctx, "notifications", "presentation_notifications", map[string]any{"identity": identity})
	if err != nil {
		return nil, err
	}
	_, snapshot, err := na.Outcome(reply, "notifications")
	if err != nil {
		return nil, fmt.Errorf("presentation_notifications: %w", err)
	}
	observed := func(section string) (map[string]any, error) {
		raw, ok := na.AsMap(snapshot[section])
		if !ok {
			return nil, fmt.Errorf("presentation_notifications: %s section absent", section)
		}
		_, value, err := na.Outcome(raw, "observed")
		if err != nil {
			return nil, fmt.Errorf("presentation_notifications: %s: %w", section, err)
		}
		return value, nil
	}
	letters, err := observed("letters")
	if err != nil {
		return nil, err
	}
	rows := na.AsSlice(letters["letters"])
	if len(rows) != len(statusLetters) {
		return nil, fmt.Errorf("typed letters %d, home/status letters %d", len(rows), len(statusLetters))
	}
	listing, _ := na.AsMap(letters["listing"])
	if int(na.AsNumber(listing["totalCount"])) != len(rows) || int(na.AsNumber(listing["returnedCount"])) != len(rows) || listing["complete"] != true {
		return nil, fmt.Errorf("typed letter listing does not describe %d rows: %#v", len(rows), listing)
	}
	var found map[string]any
	for _, raw := range rows {
		if row, ok := na.AsMap(raw); ok && na.AsString(row["id"]) == letterID {
			found = row
		}
	}
	if found == nil {
		return nil, fmt.Errorf("typed letters omit %s: %#v", letterID, rows)
	}
	if na.AsString(found["defName"]) != "PositiveEvent" || na.AsString(found["label"]) == "" || na.AsString(found["nativeType"]) == "" {
		return nil, fmt.Errorf("typed letter %s lacks def, label or type: %#v", letterID, found)
	}
	if _, err := observed("messages"); err != nil {
		return nil, err
	}
	alerts, err := observed("alerts")
	if err != nil {
		return nil, err
	}
	if na.AsString(alerts["snapshotFingerprint"]) == "" {
		return nil, fmt.Errorf("typed alerts lack a snapshot fingerprint: %#v", alerts)
	}
	// A fresh debug colony always has at least "Need colonist beds"; an empty
	// list here means the headless alert readout stopped running (#94).
	if len(na.AsSlice(alerts["alerts"])) == 0 {
		return nil, fmt.Errorf("typed alerts are empty on a fresh colony: the headless AlertsReadout is not updating")
	}
	narrowed, err := h.Wire(ctx, "notifications-no-letters", "presentation_notifications",
		map[string]any{"identity": identity, "includeLetters": false, "alertLimit": 1})
	if err != nil {
		return nil, err
	}
	_, narrowedSnapshot, err := na.Outcome(narrowed, "notifications")
	if err != nil {
		return nil, fmt.Errorf("presentation_notifications(includeLetters=false): %w", err)
	}
	if _, present := narrowedSnapshot["letters"]; present {
		return nil, fmt.Errorf("includeLetters:false still returned a letter section")
	}
	narrowedAlerts, _ := na.AsMap(narrowedSnapshot["alerts"])
	if _, ok := narrowedAlerts["observed"]; !ok {
		return nil, fmt.Errorf("includeLetters:false lost the alert section: %#v", narrowedSnapshot)
	}
	return map[string]any{"letterId": letterID, "letters": len(rows), "alerts": len(na.AsSlice(alerts["alerts"])),
		"alertFingerprint": alerts["snapshotFingerprint"]}, nil
}
