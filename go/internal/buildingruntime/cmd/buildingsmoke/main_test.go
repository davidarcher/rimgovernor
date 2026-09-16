package main

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func argsFor(t *testing.T, mode string) []string {
	t.Helper()
	d := t.TempDir()
	return []string{"--mode", mode, "--gabs", filepath.Join(d, "gabs"), "--config", d, "--profile", d, "--state", filepath.Join(d, "state.sqlite"), "--output", filepath.Join(d, "output")}
}
func TestExplicitModeAndFixtureGuard(t *testing.T) {
	args := argsFor(t, "place")
	if _, err := parse(args); err == nil {
		t.Fatal("implicit write authorized")
	}
	args = append(args, "--execute", "--request", filepath.Join(t.TempDir(), "request.json"))
	if _, err := parse(args); err != nil {
		t.Fatal(err)
	}
	if _, err := parse(append(argsFor(t, "observe"), "--execute")); err == nil {
		t.Fatal("observe accepted write flag")
	}
	if _, err := parse(argsFor(t, "observe")); err != nil {
		t.Fatal(err)
	}
	for _, data := range []string{`{}`, `{"defName":"Wall","x":0,"z":0,"rotation":"north"}`, `{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_ALL"}`, `{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_NORTH","godMode":true}`} {
		if _, err := fixture([]byte(data)); err == nil {
			t.Fatal("invalid fixture accepted")
		}
	}
	if _, err := fixture([]byte(`{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_NORTH"}`)); err != nil {
		t.Fatal(err)
	}
}
func TestStateReservationNeverOverwrites(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.sqlite")
	if err := reserveState(path, "place"); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("retained evidence"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := reserveState(path, "place"); err == nil {
		t.Fatal("existing state replaced")
	}
	if err := reserveState(path, "observe"); err == nil {
		t.Fatal("nonSQLite accepted for observation")
	}
	data, _ := os.ReadFile(path)
	if string(data) != "retained evidence" {
		t.Fatal("state changed")
	}
}

type fakeSession struct {
	acquired, observed, runs int
	err                      error
	result                   executor.Result
}

func (s *fakeSession) Acquire(_ context.Context, v domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	s.acquired++
	return v, s.err
}
func (s *fakeSession) ObserveTarget(context.Context, domain.GenerationSnapshot) error {
	s.observed++
	return s.err
}
func (s *fakeSession) Run(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
	s.runs++
	return s.result, nil
}
func TestOneAdvanceAndReadOnlyRestart(t *testing.T) {
	for _, mode := range []string{"place", "observe"} {
		s := &fakeSession{}
		if _, err := advance(context.Background(), mode, s, domain.GenerationSnapshot{}); err != nil {
			t.Fatal(err)
		}
		if s.runs != 1 || (mode == "place" && (s.acquired != 1 || s.observed != 0)) || (mode == "observe" && (s.acquired != 0 || s.observed != 1)) {
			t.Fatal("mode crossed authority boundary")
		}
	}
	s := &fakeSession{err: errors.New("authority unavailable")}
	if _, err := advance(context.Background(), "place", s, domain.GenerationSnapshot{}); err == nil || s.runs != 0 {
		t.Fatal("ran after failed control")
	}
}
func TestReportDoesNotConflateAcceptanceCompletionOrUnknown(t *testing.T) {
	spec, err := fixture([]byte(`{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_NORTH"}`))
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(spec, actionID)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: planID, Revision: 1, Native: 1}
	progress, err = progress.Prepare(snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	progress, err = progress.MarkDispatched(snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	unknown, err := progress.RecordReceipt(1, domain.ReceiptUnknown)
	if err != nil {
		t.Fatal(err)
	}
	if accepted("place", "completed", executor.Result{Progress: unknown, NativeCalled: true}) {
		t.Fatal("uncertainty passed")
	}
	progress, err = progress.RecordReceipt(1, domain.ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	if !accepted("place", "completed", executor.Result{Progress: progress, NativeCalled: true}) || accepted("observe", "completed", executor.Result{Progress: progress}) {
		t.Fatal("receipt treated as completion")
	}
	progress, err = progress.Observe(domain.Observation{Action: actionID, Attempt: 1, Snapshot: snapshot, Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if !accepted("observe", "completed", executor.Result{Progress: progress}) || accepted("place", "completed", executor.Result{Progress: progress, NativeCalled: true}) {
		t.Fatal("completion misclassified")
	}
	v := progress.View()
	v.Attempt = domain.AttemptID(^uint64(0))
	v.Receipt = domain.Unknown[domain.Receipt]()
	data, err := json.Marshal(project(v))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), `"Attempt":"18446744073709551615"`) || !strings.Contains(string(data), `"Receipt":null`) || strings.Contains(string(data), `"Effect":{}`) {
		t.Fatal(string(data))
	}
}

func TestExpectedOutcomeParserAndExactTerminalObservation(t *testing.T) {
	for _, expected := range []string{"completed", "cancelled", "interrupted"} {
		opts, err := parse(append(argsFor(t, "observe"), "--expected-outcome", expected))
		if err != nil || opts.expectedOutcome != expected {
			t.Fatal(opts, err)
		}
		args := append(argsFor(t, "place"), "--execute", "--request", filepath.Join(t.TempDir(), "request.json"), "--expected-outcome", expected)
		_, err = parse(args)
		if (err == nil) != (expected == "completed") {
			t.Fatal(expected, err)
		}
	}
	if _, err := parse(append(argsFor(t, "observe"), "--expected-outcome", "failed")); err == nil {
		t.Fatal("unknown expectation accepted")
	}
	spec, err := fixture([]byte(`{"defName":"Wall","x":0,"z":0,"rotation":"ROTATION_NORTH"}`))
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: planID, Revision: 1, Native: 1}
	for _, reason := range []domain.UnsuccessfulReason{domain.NativeCancelled, domain.NativeInterrupted, domain.NativeFailure, domain.NativeExpired} {
		progress, err := domain.NewProgress(spec, actionID)
		if err != nil {
			t.Fatal(err)
		}
		progress, err = progress.Prepare(snapshot, 10)
		if err != nil {
			t.Fatal(err)
		}
		progress, err = progress.MarkDispatched(snapshot, 10)
		if err != nil {
			t.Fatal(err)
		}
		progress, err = progress.RecordReceipt(1, domain.ReceiptUnknown)
		if err != nil {
			t.Fatal(err)
		}
		for _, expected := range []string{"completed", "cancelled", "interrupted"} {
			if accepted("observe", expected, executor.Result{Progress: progress}) {
				t.Fatal("uncertain receipt passed")
			}
		}
		progress, err = progress.Observe(domain.Observation{Action: actionID, Attempt: 1, Snapshot: snapshot, Tick: 11, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: reason, Causality: domain.AfterDispatch}, snapshot)
		if err != nil {
			t.Fatal(err)
		}
		fake := &fakeSession{result: executor.Result{Progress: progress}}
		result, err := advance(context.Background(), "observe", fake, snapshot)
		if err != nil || fake.acquired != 0 || fake.observed != 1 || fake.runs != 1 {
			t.Fatal("observe crossed authority boundary", err)
		}
		for _, expected := range []string{"completed", "cancelled", "interrupted"} {
			want := (expected == "cancelled" && reason == domain.NativeCancelled) || (expected == "interrupted" && reason == domain.NativeInterrupted)
			if accepted("observe", expected, result) != want {
				t.Fatal(expected, reason)
			}
			result.NativeCalled = true
			if accepted("observe", expected, result) {
				t.Fatal("write passed observe")
			}
			result.NativeCalled = false
		}
		bytes, err := json.Marshal(report{Mode: "observe", ExpectedOutcome: string(reason), Progress: project(progress.View())})
		if err != nil || !strings.Contains(string(bytes), `"expectedOutcome":"`+string(reason)+`"`) {
			t.Fatal(string(bytes), err)
		}
	}
	fake := &fakeSession{err: errors.New("read failed")}
	if _, err := advance(context.Background(), "observe", fake, snapshot); err == nil || fake.runs != 0 || fake.acquired != 0 {
		t.Fatal("failed observation ran")
	}
}
